namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using System.Text.Json;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit tests for DefinitionOfDoneGate: in-dock build and unit-test gate for Worker missions.
    /// </summary>
    public partial class DefinitionOfDoneGateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Definition Of Done Gate";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Consumer verification links a sibling's extraction artifacts and removing the link leaves the source tree", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-dod-artifacts-" + Guid.NewGuid().ToString("N"));
                string host = Path.Combine(root, "host-deobfuscator");
                string worktree = Path.Combine(root, "verify", "Deobfuscator");
                try
                {
                    Directory.CreateDirectory(Path.Combine(host, "output", "decompiled-src", "Vendor"));
                    string sourceFile = Path.Combine(host, "output", "decompiled-src", "Vendor", "Form.cs");
                    File.WriteAllText(sourceFile, "class Form {}");
                    Directory.CreateDirectory(worktree);
                    // The worktree already tracks a partial tree at the artifact path, as a deobfuscator does.
                    Directory.CreateDirectory(Path.Combine(worktree, "output", "decompiled-src"));
                    File.WriteAllText(Path.Combine(worktree, "output", "decompiled-src", "Tracked.cs"), "class Tracked {}");

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;
                        DefinitionOfDoneGate gate = new DefinitionOfDoneGate(new DefinitionOfDoneSettings(), testDb.Driver, logging);
                        SiblingRepo sibling = new SiblingRepo
                        {
                            RelativePath = "../Deobfuscator",
                            ExtractionArtifactPaths = new List<string> { "output/decompiled-src", "output/absent-export" }
                        };
                        Vessel siblingVessel = new Vessel("Deobfuscator", "https://example.invalid/deobfuscator.git") { WorkingDirectory = host };

                        List<string> links = new List<string>();
                        gate.LinkSiblingArtifacts(sibling, siblingVessel, worktree, links);

                        AssertEqual(1, links.Count, "the present artifact is linked and the absent one is skipped");
                        AssertTrue(new DirectoryInfo(Path.Combine(worktree, "output", "decompiled-src")).LinkTarget != null, "a partial tracked tree is replaced by a link to the whole host tree");
                        AssertTrue(File.Exists(Path.Combine(worktree, "output", "decompiled-src", "Vendor", "Form.cs")), "the consumer reads the tree through the link");

                        gate.RemoveArtifactLinks(links);
                        AssertFalse(Directory.Exists(Path.Combine(worktree, "output", "decompiled-src")), "the link is removed");
                        AssertTrue(File.Exists(sourceFile), "removing the link never touches the source tree");

                        // A recursive delete of the verification root after cleanup cannot reach the host tree.
                        Directory.Delete(Path.Combine(root, "verify"), true);
                        AssertTrue(File.Exists(sourceFile), "the host tree survives the scratch cleanup");
                    }
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            });

            // Regression: two gates used to run their full build and unit-test commands at the same
            // time on one host. Mission status cannot prevent it -- WorkProduced is persisted when the
            // agent finishes, before the gate starts, and the vessel-mutex query counts only
            // Assigned/InProgress, so a vessel reads as idle for the whole duration of its own gate.
            // The loser of that race fails with a burst of sub-millisecond failures across unrelated
            // test classes and a Timeout classification, and its downstream pipeline is cancelled.
            //
            // The command below fails outright if a second gate is inside the critical section with
            // it, so this test detects overlap directly rather than inferring it from timing.
            await RunTest("Two concurrent gates never execute their commands at the same time", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreeA = CreateTempDir();
                string worktreeB = CreateTempDir();
                string lockPath = Path.Combine(Path.GetTempPath(), "armada-gate-overlap-" + Guid.NewGuid().ToString("N"));

                try
                {
                    string exclusiveCommand = MutuallyExclusiveCommand(lockPath);

                    await EnsureVesselWithProfileAsync(testDb, "ten_gate_lock", "vsl_gate_lock_a",
                        worktreeA, SuccessCommand(), exclusiveCommand, null, "Gate Lock Vessel A").ConfigureAwait(false);
                    await EnsureVesselWithProfileAsync(testDb, "ten_gate_lock", "vsl_gate_lock_b",
                        worktreeB, SuccessCommand(), exclusiveCommand, null, "Gate Lock Vessel B").ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false };

                    // Separate instances on separate vessels: the lock must be host-wide, so a
                    // per-instance or per-vessel lock would let this test through.
                    DefinitionOfDoneGate gateA = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    DefinitionOfDoneGate gateB = new DefinitionOfDoneGate(settings, testDb.Driver, logging);

                    Task<DefinitionOfDoneResult> runA = gateA.EvaluateAsync(
                        CreateWorkerMission("ten_gate_lock", "vsl_gate_lock_a"),
                        new Dock { WorktreePath = worktreeA });
                    Task<DefinitionOfDoneResult> runB = gateB.EvaluateAsync(
                        CreateWorkerMission("ten_gate_lock", "vsl_gate_lock_b"),
                        new Dock { WorktreePath = worktreeB });

                    DefinitionOfDoneResult[] results = await Task.WhenAll(runA, runB).ConfigureAwait(false);

                    AssertTrue(results[0].Passed, "First gate must pass; a failure here means the two gates overlapped");
                    AssertTrue(results[1].Passed, "Second gate must pass; a failure here means the two gates overlapped");
                    AssertFalse(File.Exists(lockPath), "Overlap sentinel must be cleaned up by both runs");
                }
                finally
                {
                    TryDeleteDirectory(worktreeA);
                    TryDeleteDirectory(worktreeB);
                    try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
                }
            }).ConfigureAwait(false);

            // A gate takes the host slot at its first command and keeps it until it ends. A check that asks while
            // the gate builds therefore runs after the gate's unit tests, instead of splitting the gate and
            // sending it to the back of the queue for each command.
            // A gate that passes vouches for each command it ran in full on a clean worktree at a known HEAD, so an
            // armed check of the same command on that commit need not run the suite again. A failed gate, or a
            // worktree with an uncommitted change, vouches for nothing.
            await RunTest("A passing gate records the commands it ran in full at the worktree HEAD; a failed gate or a dirty worktree records none", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string passWorktree = CreateTempDir();
                string failWorktree = CreateTempDir();
                string dirtyWorktree = CreateTempDir();
                string budgetWorktree = CreateTempDir();
                string head = new string('a', 40);
                try
                {
                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false };
                    Dictionary<string, string> environment = new Dictionary<string, string> { ["ARMADA_GATE_TEST_MODE"] = "profile-value", ["CONTEXT"] = "one\nNEXT=two" };
                    string build = OperatingSystem.IsWindows()
                        ? "echo %ARMADA_GATE_TEST_MODE%>build-mode.txt"
                        : "printf '%s' \"$ARMADA_GATE_TEST_MODE\" > build-mode.txt";
                    string test = OperatingSystem.IsWindows()
                        ? "echo %ARMADA_GATE_TEST_MODE%>test-mode.txt"
                        : "printf '%s' \"$ARMADA_GATE_TEST_MODE\" > test-mode.txt";

                    await EnsureVesselWithProfileAsync(testDb, "ten_pass_rec", "vsl_pass_rec", passWorktree, build, test, null, "Pass Record Vessel", environmentVariables: environment).ConfigureAwait(false);
                    Mission passing = CreateWorkerMission("ten_pass_rec", "vsl_pass_rec");
                    DefinitionOfDoneGate passGate = new DefinitionOfDoneGate(settings, testDb.Driver, logging, null,
                        new StubGitService { HeadCommitHashResult = head, IsWorkingDirectoryCleanResult = true });
                    DefinitionOfDoneResult passed = await passGate.EvaluateAsync(passing, new Dock { WorktreePath = passWorktree }).ConfigureAwait(false);
                    AssertTrue(passed.Passed, "the gate passes");
                    AssertEqual("profile-value", File.ReadAllText(Path.Combine(passWorktree, "build-mode.txt")).Trim(), "build reads the profile environment");
                    AssertEqual("profile-value", File.ReadAllText(Path.Combine(passWorktree, "test-mode.txt")).Trim(), "tests read the profile environment");

                    DefinitionOfDoneCommandPass? buildPass = DefinitionOfDoneCommandPasses.Find("vsl_pass_rec", head, build, environment);
                    DefinitionOfDoneCommandPass? testPass = DefinitionOfDoneCommandPasses.Find("vsl_pass_rec", head, test, environment);
                    AssertNotNull(buildPass, "the build command is recorded at the worktree HEAD");
                    AssertNotNull(testPass, "the unit-test command is recorded at the worktree HEAD");
                    AssertNull(DefinitionOfDoneCommandPasses.Find("vsl_pass_rec", head, test,
                        new Dictionary<string, string> { ["ARMADA_GATE_TEST_MODE"] = "profile-value", ["CONTEXT"] = "one", ["NEXT"] = "two" }),
                        "a newline in one value cannot impersonate a second variable");
                    AssertEqual(passing.Id, testPass!.MissionId, "the pass names the gate's mission");
                    AssertEqual("unit-test", testPass.Label);
                    AssertNull(DefinitionOfDoneCommandPasses.Find("vsl_pass_rec", new string('b', 40), test, null), "a pass is bound to its commit");
                    AssertNull(DefinitionOfDoneCommandPasses.Find("vsl_pass_rec", head, test, new Dictionary<string, string> { ["MODE"] = "ci" }),
                        "a pass is bound to the environment the command ran with");

                    await EnsureVesselWithProfileAsync(testDb, "ten_fail_rec", "vsl_fail_rec", failWorktree, build, FailCommand(), null, "Fail Record Vessel").ConfigureAwait(false);
                    DefinitionOfDoneGate failGate = new DefinitionOfDoneGate(settings, testDb.Driver, logging, null,
                        new StubGitService { HeadCommitHashResult = head, IsWorkingDirectoryCleanResult = true });
                    DefinitionOfDoneResult failed = await failGate.EvaluateAsync(
                        CreateWorkerMission("ten_fail_rec", "vsl_fail_rec"), new Dock { WorktreePath = failWorktree }).ConfigureAwait(false);
                    AssertFalse(failed.Passed, "the gate fails on its unit tests");
                    AssertNull(DefinitionOfDoneCommandPasses.Find("vsl_fail_rec", head, build, null), "a failed gate records nothing, not even the build it passed");

                    await EnsureVesselWithProfileAsync(testDb, "ten_dirty_rec", "vsl_dirty_rec", dirtyWorktree, build, test, null, "Dirty Record Vessel").ConfigureAwait(false);
                    DefinitionOfDoneGate dirtyGate = new DefinitionOfDoneGate(settings, testDb.Driver, logging, null,
                        new StubGitService { HeadCommitHashResult = head, IsWorkingDirectoryCleanResult = false });
                    DefinitionOfDoneResult dirty = await dirtyGate.EvaluateAsync(
                        CreateWorkerMission("ten_dirty_rec", "vsl_dirty_rec"), new Dock { WorktreePath = dirtyWorktree }).ConfigureAwait(false);
                    AssertTrue(dirty.Passed, "the gate passes");
                    AssertNull(DefinitionOfDoneCommandPasses.Find("vsl_dirty_rec", head, test, null), "an uncommitted change means the HEAD does not describe what ran");

                    // A real full gate at an explicit budget still saves the matching automatic
                    // Check. The command fails on a second invocation, so fallback execution is red.
                    const string budgetUser = "usr_gate_budget_rec";
                    await testDb.Driver.Users.CreateAsync(new UserMaster
                    {
                        Id = budgetUser, TenantId = "ten_pass_rec", Email = "gate-budget-rec@armada.test",
                        PasswordSha256 = UserMaster.ComputePasswordHash("test-password"), Active = true
                    }).ConfigureAwait(false);
                    Vessel budgetVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Budget proof vessel", String.Empty)
                    {
                        TenantId = "ten_pass_rec", UserId = budgetUser, WorkingDirectory = budgetWorktree,
                        LocalPath = String.Empty, DefaultBranch = "main"
                    }).ConfigureAwait(false);
                    string budgetCommand = OperatingSystem.IsWindows()
                        ? "if exist command-ran.txt (exit /b 17) else (echo once>command-ran.txt)"
                        : "if [ -e command-ran.txt ]; then exit 17; fi; printf once > command-ran.txt";
                    WorkflowProfile budgetProfile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                    {
                        TenantId = "ten_pass_rec", UserId = budgetUser, Name = "Budget proof profile",
                        Scope = WorkflowProfileScopeEnum.Vessel, VesselId = budgetVessel.Id,
                        UnitTestCommand = budgetCommand, GateCommandTimeoutSeconds = 600, IsDefault = true, Active = true
                    }).ConfigureAwait(false);
                    Mission budgetMission = CreateWorkerMission("ten_pass_rec", budgetVessel.Id);
                    budgetMission.UserId = budgetUser;
                    budgetMission.Status = MissionStatusEnum.WorkProduced;
                    await testDb.Driver.Missions.CreateAsync(budgetMission).ConfigureAwait(false);
                    DefinitionOfDoneGate budgetGate = new DefinitionOfDoneGate(settings, testDb.Driver, logging, null,
                        new StubGitService { HeadCommitHashResult = head, IsWorkingDirectoryCleanResult = true });
                    DefinitionOfDoneResult budgetGateResult = await budgetGate.EvaluateAsync(budgetMission,
                        new Dock { WorktreePath = budgetWorktree }).ConfigureAwait(false);
                    AssertTrue(budgetGateResult.Passed, "The actual full command passes with its declared 600-second budget.");
                    long budgetTicks = TimeSpan.FromSeconds(600).Ticks;
                    DefinitionOfDoneCommandPass? budgetProof = DefinitionOfDoneCommandPasses.Find(
                        budgetVessel.Id, head, budgetCommand, null, budgetTicks);
                    AssertNotNull(budgetProof, "The actual gate publishes a pass tagged with its execution budget.");
                    AssertEqual(budgetTicks, budgetProof!.ExecutionBudgetTicks ?? -1);
                    AssertNull(DefinitionOfDoneCommandPasses.Find(budgetVessel.Id, head, budgetCommand, null),
                        "A declared-budget pass does not impersonate the undeclared contract.");
                    WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                    CheckRunService checks = new CheckRunService(testDb.Driver, profiles,
                        new VesselReadinessService(testDb.Driver, profiles, logging), logging);
                    CheckRun armedBudget = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        TenantId = "ten_pass_rec", UserId = budgetUser, VesselId = budgetVessel.Id,
                        MissionId = budgetMission.Id, WorkflowProfileId = budgetProfile.Id,
                        Type = CheckRunTypeEnum.UnitTest, Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending, CommitHash = head, Command = budgetCommand,
                        WorkingDirectory = budgetWorktree
                    }).ConfigureAwait(false);
                    CheckRun adoptedBudget = await checks.RunPendingAsync(
                        AuthContext.Authenticated("ten_pass_rec", budgetUser, false, false, "UnitTest"), armedBudget.Id).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, adoptedBudget.Status, "The matching-budget automatic Check adopts the real gate proof.");
                    AssertNotNull(adoptedBudget.SatisfiedBy, "Adoption names the actual gate rather than executing the command twice.");
                    AssertEqual(budgetMission.Id, adoptedBudget.SatisfiedBy!.MissionId);
                    AssertEqual(adoptedBudget.CompletedUtc, adoptedBudget.StartedUtc,
                        "Adoption keeps the existing instantaneous lifecycle stamp without an execution duration.");
                    AssertNull(adoptedBudget.DurationMs, "An adopted Check has no invented execution duration.");
                    AssertEqual("once", File.ReadAllText(Path.Combine(budgetWorktree, "command-ran.txt")).Trim(),
                        "The command ran once through the real gate.");
                }
                finally
                {
                    TryDeleteDirectory(passWorktree);
                    TryDeleteDirectory(failWorktree);
                    TryDeleteDirectory(dirtyWorktree);
                    TryDeleteDirectory(budgetWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("A check that asks for the host slot during a gate build runs after the gate's tests", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                Func<long?> memoryReaderBefore = HostWideCommandLock.MemoryReader;
                int memoryFloorBefore = HostWideCommandLock.MemoryFloorMb;
                int admissionReadings = 0;
                DefinitionOfDoneCommandPass? passAtProbeAdmission = null;
                string head = new string('c', 40);
                string testCommand = EchoToFileCommand("tests-ran.txt", "ran");
                try
                {
                    // The reader runs synchronously when each waiting command is admitted. Observe the
                    // pass before releasing the gate can schedule either caller's continuation.
                    HostWideCommandLock.SetMemoryReader(() =>
                    {
                        admissionReadings++;
                        if (admissionReadings == 2)
                            passAtProbeAdmission = DefinitionOfDoneCommandPasses.Find("vsl_gate_fair", head, testCommand, null);
                        return 1024;
                    });
                    HostWideCommandLock.SetMemoryFloorMb(1);
                    string slowBuild = OperatingSystem.IsWindows() ? "timeout /t 2 /nobreak > nul" : "sleep 1";
                    await EnsureVesselWithProfileAsync(testDb, "ten_gate_fair", "vsl_gate_fair",
                        worktreePath, slowBuild, testCommand).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging,
                        gitService: new StubGitService { HeadCommitHashResult = head, IsWorkingDirectoryCleanResult = true });
                    Task<DefinitionOfDoneResult> gateRun = gate.EvaluateAsync(
                        CreateWorkerMission("ten_gate_fair", "vsl_gate_fair"),
                        new Dock { WorktreePath = worktreePath });

                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while ((HostWideCommandLock.Snapshot().HolderDescription ?? String.Empty).IndexOf("definition-of-done gate", StringComparison.Ordinal) < 0)
                    {
                        AssertTrue(DateTime.UtcNow < deadline, "the gate never took the host slot");
                        await Task.Delay(10).ConfigureAwait(false);
                    }

                    bool testsRanFirst;
                    using (await HostWideCommandLock.AcquireAsync("chk_fair_probe", "check probe", CancellationToken.None).ConfigureAwait(false))
                    {
                        testsRanFirst = File.Exists(Path.Combine(worktreePath, "tests-ran.txt"));
                        AssertNotNull(passAtProbeAdmission, "the gate published its full pass before granting the queued command's slot");
                        AssertNotNull(DefinitionOfDoneCommandPasses.Find("vsl_gate_fair", head, testCommand, null), "the released slot exposes the gate pass immediately");
                    }

                    DefinitionOfDoneResult result = await gateRun.ConfigureAwait(false);
                    AssertTrue(result.Passed, "the gate passes");
                    AssertTrue(testsRanFirst, "the gate keeps the slot through its unit tests, ahead of a check that asked later");
                    AssertTrue(File.Exists(Path.Combine(worktreePath, "tests-ran.txt")), "the gate's unit tests still run");
                }
                finally
                {
                    HostWideCommandLock.SetMemoryFloorMb(memoryFloorBefore);
                    HostWideCommandLock.SetMemoryReader(memoryReaderBefore);
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            // A test that loses a race under the loaded suite passes when its class runs alone; a real
            // defect fails again. The vessel's own red is re-run once in isolation and that result stands.
            await RunTest("A vessel test that fails in the suite and passes alone does not fail the gate", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // Fails on its first run, passes once it has run before. The trailing dotnet test is never
                    // executed; it only lets the gate form the class-filtered re-run command.
                    string raceOnce = "bash -c 'if [ -e race.marker ]; then echo ok; exit 0; fi; touch race.marker; "
                        + "echo \"  Failed Example.Tests.ConsoleRaceTests.Prints_Its_Line [1 ms]\"; "
                        + "echo \"Failed!  - Failed: 1, Passed: 0, Total: 1\"; exit 1; dotnet test Example.sln --filter \"Category!=Integration\"'";
                    await EnsureVesselWithProfileAsync(testDb, "ten_rerun_once", "vsl_rerun_once",
                        worktreePath, SuccessCommand(), raceOnce).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_rerun_once", "vsl_rerun_once"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
                    AssertTrue(result.Passed, "a failing class that passes alone does not fail the gate");
                    AssertNull(result.GateCommandOutput, "an isolated pass cannot supply a full-suite pass capture or leave the build as its substitute");

                    File.Delete(Path.Combine(worktreePath, "race.marker"));
                    DefinitionOfDoneGate off = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, RerunFailingClassesOnce = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult red = await off.EvaluateAsync(
                        CreateWorkerMission("ten_rerun_once", "vsl_rerun_once"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
                    AssertFalse(red.Passed, "with the setting off the suite red stands");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, red.FailureClass);
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            // The isolated re-run shares the host with every other gate, check and captain, so a fail alone on a busy
            // host is not proof the failure is unrelated to load; the text must say so and carry the load it ran under.
            await RunTest("A vessel test that fails again alone on a busy host fails the gate and says the failure may still be load-related", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string alwaysRed = "bash -c 'echo \"  Failed Example.Tests.ParserTests.Reads_The_Header [1 ms]\"; "
                        + "echo \"Failed!  - Failed: 1, Passed: 0, Total: 1\"; exit 1; dotnet test Example.sln --filter \"Category!=Integration\"'";
                    await EnsureVesselWithProfileAsync(testDb, "ten_rerun_red", "vsl_rerun_red",
                        worktreePath, SuccessCommand(), alwaysRed).ConfigureAwait(false);

                    // Above the core count but below the overload threshold: busy, yet no admission wait.
                    HostLoadMonitor busyHost = new HostLoadMonitor(() => 6.0, () => 4, longLoadReader: () => 5.0);
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging,
                        hostLoad: busyHost);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_rerun_red", "vsl_rerun_red"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    string tail = result.OutputTail ?? String.Empty;
                    AssertFalse(result.Passed, "a test that fails again alone keeps the red");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, result.FailureClass);
                    AssertContains("Isolated re-run of 1 failing class(es) under the current host load also failed.", tail,
                        "the failure says the isolated re-run also failed under the current host load");
                    AssertFalse(tail.Contains("not a load race", StringComparison.Ordinal), "a re-run on the same host never rules out a load race");
                    AssertContains("load average 6.00/unknown/5.00 (1/5/15 min) on 4 CPUs", tail, "the load averages and CPU count at the re-run are recorded");
                    AssertContains("MemAvailable unknown", tail, "the available memory at the re-run is recorded");
                    AssertContains("captain(s) working", tail, "the working captains at the re-run are recorded");
                    AssertContains("may still be load-related", tail, "a busy host leaves load as a possible cause");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            // A runner whose failure lines no built-in matcher reads gets its failing tests re-run alone through
            // the profile's own command. The names travel only in ARMADA_RERUN_TESTS, never in the command text.
            await RunTest("A profile re-run command receives the failing names only through ARMADA_RERUN_TESTS and its pass clears the red", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string hostile = "a'; touch pwned; echo '";
                    string testCommand = WriteExampleRunner(worktreePath, hostile, "Prints the header when the file is empty");
                    await EnsureVesselWithProfileAsync(testDb, "ten_profile_rerun", "vsl_profile_rerun",
                        worktreePath, SuccessCommand(), testCommand,
                        failedTestLinePattern: _ExampleRunnerPattern,
                        isolatedTestRerunCommand: "printf '%s' \"$" + IsolatedRerunPlan.NamesEnvironmentVariable + "\" > rerun-names.txt").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_profile_rerun", "vsl_profile_rerun"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "a passing isolated re-run clears the red and the gate continues");
                    string namesPath = Path.Combine(worktreePath, "rerun-names.txt");
                    AssertTrue(File.Exists(namesPath), "the profile's isolated re-run command ran");
                    AssertEqual(hostile + "\nPrints the header when the file is empty", File.ReadAllText(namesPath),
                        "the variable carries the exact names, one per line, in output order");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "pwned")),
                        "a name with shell metacharacters is never run as command text");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A profile re-run command that fails again keeps the red with the also-failed-alone text and names the failing test", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string testCommand = WriteExampleRunner(worktreePath, "Rejects an empty header");
                    await EnsureVesselWithProfileAsync(testDb, "ten_profile_rerun_red", "vsl_profile_rerun_red",
                        worktreePath, SuccessCommand(), testCommand,
                        failedTestLinePattern: _ExampleRunnerPattern,
                        isolatedTestRerunCommand: "echo \"FAIL Rejects an empty header: Sequence contains no elements\"; "
                            + "i=0; while [ $i -lt 120 ]; do echo \"ok $i - a later suite passes\"; i=$((i+1)); done; exit 1").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_profile_rerun_red", "vsl_profile_rerun_red"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "a failing isolated re-run keeps the red");
                    AssertContains("Isolated re-run of 1 failing test(s) under the current host load also failed", result.OutputTail ?? String.Empty,
                        "the red says the isolated re-run also failed");
                    // The re-run prints its failure first and many passing lines after it, past the output tail.
                    AssertContains("Rejects an empty header", result.OutputTail ?? String.Empty,
                        "the re-run's failure text names the failing test read with the profile pattern, not only the passing tail");
                    AssertTrue(result.FailedTestNames != null && result.FailedTestNames.Contains("Rejects an empty header"),
                        "the re-run result carries the failing test name");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("More failing names than the bound are not re-run through the profile command and the bound is named", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string testCommand = WriteExampleRunner(worktreePath, "First case", "Second case", "Third case", "Fourth case");
                    await EnsureVesselWithProfileAsync(testDb, "ten_profile_bound", "vsl_profile_bound",
                        worktreePath, SuccessCommand(), testCommand,
                        failedTestLinePattern: _ExampleRunnerPattern,
                        isolatedTestRerunCommand: "touch rerun-ran.marker").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_profile_bound", "vsl_profile_bound"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "the red stands");
                    AssertContains("Not re-run in isolation: 4 failing tests exceed the bound of 3", result.OutputTail ?? String.Empty,
                        "the reason names the count and the bound");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "rerun-ran.marker")), "the profile command does not run");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A pattern that exceeds its match timeout reports the names unreadable instead of hanging", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    File.WriteAllText(Path.Combine(worktreePath, "run-tests.sh"),
                        "#!/bin/sh\nprintf '%s\\n' \"FAIL " + new string('a', 40) + "!\" >&2\nexit 1\n");
                    await EnsureVesselWithProfileAsync(testDb, "ten_pattern_slow", "vsl_pattern_slow",
                        worktreePath, SuccessCommand(), "sh ./run-tests.sh",
                        failedTestLinePattern: @"^FAIL (?<name>(\w+\s?)+)$",
                        isolatedTestRerunCommand: "touch rerun-ran.marker").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_pattern_slow", "vsl_pattern_slow"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "the red stands");
                    AssertTrue(result.FailedTestNamesOverflow, "the timed-out read is marked incomplete");
                    AssertContains("Not re-run in isolation: the failing test names could not be read in full", result.OutputTail ?? String.Empty,
                        "the gate reports the names unreadable");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "rerun-ran.marker")), "nothing is re-run");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Names read from a script command with no profile re-run command get the no-re-run-route reason", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string testCommand = WriteExampleRunner(worktreePath, "Prints the header when the file is empty");
                    await EnsureVesselWithProfileAsync(testDb, "ten_noroute", "vsl_noroute",
                        worktreePath, SuccessCommand(), testCommand,
                        failedTestLinePattern: _ExampleRunnerPattern).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_noroute", "vsl_noroute"),
                        new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "the red stands");
                    AssertEqual(1, result.FailedTestNames?.Count ?? 0, "the profile pattern read the name");
                    AssertContains("Not re-run in isolation: " + IsolatedRerunPlan.NoRerunRouteReason, result.OutputTail ?? String.Empty,
                        "the reason says which field is missing");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate passes when both build and unit-test commands succeed", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_pass", "vsl_pass",
                        worktreePath, "echo BUILD-PROOF", "echo UNIT-FOOTER").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_pass", "vsl_pass");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should pass when both commands succeed");
                    AssertNull(result.CommandLabel, "No command should be labeled as failing");
                    AssertNull(result.OutputTail, "passing output is not a failure tail");
                    AssertContains("unit-test", result.GateCommandOutput ?? String.Empty);
                    AssertContains("UNIT-FOOTER", result.GateCommandOutput ?? String.Empty);
                    AssertFalse((result.GateCommandOutput ?? String.Empty).Contains("BUILD-PROOF", StringComparison.Ordinal),
                        "the retained capture belongs to the last full vessel command");
                    AssertFalse(result.RunnerOutputIncomplete);

                    WorkflowProfile profile = (await testDb.Driver.WorkflowProfiles.EnumerateAllAsync(
                        new WorkflowProfileQuery { TenantId = "ten_pass", PageSize = 10 }).ConfigureAwait(false)).Single();
                    profile.UnitTestCommand = null;
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                    DefinitionOfDoneResult buildOnly = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                    AssertTrue(buildOnly.Passed);
                    AssertContains("build", buildOnly.GateCommandOutput ?? String.Empty);
                    AssertContains("BUILD-PROOF", buildOnly.GateCommandOutput ?? String.Empty);
                    AssertNull(buildOnly.CommandLabel);
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate commands receive the objective's provisioned sibling pin, and a pin that does not resolve stops the gate", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                string producerRoot = CreateTempDir();
                try
                {
                    string producerRepo = Path.Combine(producerRoot, "producer");
                    GitServiceTests.SeedTemplateRepository(producerRepo);
                    GitService git = new GitService(logging);
                    string fullPin = (await git.GetRevisionCommitShaAsync(producerRepo, "HEAD").ConfigureAwait(false))!;

                    string build = OperatingSystem.IsWindows()
                        ? "echo %ARMADA_SIBLING_PIN_PRODUCER%>pin.txt"
                        : "printf '%s' \"$ARMADA_SIBLING_PIN_PRODUCER\" > pin.txt";
                    await EnsureVesselWithProfileAsync(testDb, "ten_pin_env", "vsl_pin_env", worktreePath, build, "echo UNIT", null, "Pin Env Vessel").ConfigureAwait(false);
                    Vessel producer = await testDb.Driver.Vessels.CreateAsync(new Vessel("pin-env-producer", producerRepo) { TenantId = "ten_pin_env", LocalPath = producerRepo, DefaultBranch = "main" }).ConfigureAwait(false);
                    Vessel consumer = (await testDb.Driver.Vessels.ReadAsync("vsl_pin_env").ConfigureAwait(false))!;
                    consumer.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = producer.Id, RelativePath = "../Producer", BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly, DefaultBranch = "main" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(consumer).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("pin env voyage") { TenantId = "ten_pin_env" }).ConfigureAwait(false);
                    Objective objective = new Objective { TenantId = "ten_pin_env", Title = "measure against the pinned producer" };
                    objective.VoyageIds.Add(voyage.Id);
                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = producer.Id,
                        RelativePath = "../Producer",
                        PinnedCommit = fullPin.Substring(0, 12),
                        ProvisionPinnedCommit = true
                    });
                    objective = await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    Mission mission = CreateWorkerMission("ten_pin_env", "vsl_pin_env");
                    mission.VoyageId = voyage.Id;
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false }, testDb.Driver, logging, null, git);
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                    AssertTrue(result.Passed, "the gate passes: " + result.OutputTail);
                    AssertEqual(fullPin, File.ReadAllText(Path.Combine(worktreePath, "pin.txt")).Trim(),
                        "the build command receives the pin, resolved to the full commit");

                    File.Delete(Path.Combine(worktreePath, "pin.txt"));
                    objective.Preparation.RequiredSiblingInputs[0].PinnedCommit = "deadbeefdeadbeef";
                    await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);
                    DefinitionOfDoneResult unresolved = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                    AssertFalse(unresolved.Passed, "an unresolved pin stops the gate");
                    AssertEqual("sibling-pin", unresolved.CommandLabel);
                    AssertContains(SiblingPinOverride.UnresolvedPrefix, unresolved.OutputTail ?? String.Empty);
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "pin.txt")), "no command runs against an unresolved pin");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                    TryDeleteDirectory(producerRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate holds a dock lease across execution and releases it afterwards", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // A deliberately slow build command so the lease is observable while the gate runs.
                    await EnsureVesselWithProfileAsync(testDb, "ten_lease", "vsl_lease",
                        worktreePath, "sleep 2", "true").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_lease", "vsl_lease");
                    Dock dock = new Dock { WorktreePath = worktreePath };
                    AssertFalse(DockLeaseRegistry.IsHeld(dock.Id), "No lease before the gate runs");

                    Task<DefinitionOfDoneResult> run = gate.EvaluateAsync(mission, dock, CancellationToken.None);

                    // Poll until the gate is inside the critical section (lease acquired before the
                    // host-wide wait), then assert the lease is genuinely held mid-execution.
                    bool observedHeld = false;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline && !run.IsCompleted)
                    {
                        if (DockLeaseRegistry.IsHeld(dock.Id))
                        {
                            observedHeld = true;
                            break;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }

                    AssertTrue(observedHeld, "The gate must hold the dock lease while it executes");
                    DefinitionOfDoneResult result = await run.ConfigureAwait(false);
                    AssertTrue(result.Passed, "Gate should pass when both commands succeed");
                    AssertFalse(DockLeaseRegistry.IsHeld(dock.Id), "The gate must release the lease after execution");
                    AssertEqual(0, DockLeaseRegistry.LeaseCount(dock.Id), "Lease count must return to zero");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate fails when build command exits non-zero", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_buildfail", "vsl_buildfail",
                        worktreePath, FailCommand(), SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_buildfail", "vsl_buildfail");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when build command fails");
                    AssertEqual("build", result.CommandLabel, "CommandLabel should identify the build step");
                    AssertTrue(result.ExitCode != 0, "ExitCode should be non-zero for a failed build");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate fails when unit-test command exits non-zero", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_testfail", "vsl_testfail",
                        worktreePath, SuccessCommand(), FailCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_testfail", "vsl_testfail");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when unit-test command fails");
                    AssertEqual("unit-test", result.CommandLabel, "CommandLabel should identify the unit-test step");
                    AssertTrue(result.ExitCode != 0, "ExitCode should be non-zero for a failed unit test");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate records the failing test names from a TestFail unit-test run", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_testnames", "vsl_testnames",
                        worktreePath, SuccessCommand(), FailingTestNamesCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_testnames", "vsl_testnames");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail on a failing unit-test run");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, result.FailureClass!.Value, "The failure is a test failure");
                    AssertNotNull(result.FailedTestNames, "A TestFail result must carry the failing test names");
                    AssertFalse(result.FailedTestNamesOverflow, "A two-test run does not overflow");
                    AssertEqual(2, result.FailedTestNames!.Count, "Both failing tests are recorded");
                    AssertEqual("X.Y.FirstTest", result.FailedTestNames[0], "First failing test name is captured whole");
                    AssertEqual("X.Y.SecondTest", result.FailedTestNames[1], "Second failing test name is captured whole");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("The failure text names every failing test with its first exception line and every skip, from stdout and stderr", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    string worktreePath = CreateTempDir();
                    try
                    {
                        // The early failure sits behind more diagnostics than the gate keeps, and the
                        // stderr failure is followed by enough stderr output to push it out of the tail.
                        string command = "i=0; while [ \"$i\" -lt 40 ]; do echo \"error CS1000: synthetic diagnostic $i\"; i=$((i+1)); done; "
                            + "echo '  Failed X.Y.EarlyTest [1 ms]'; echo '  Error Message:'; "
                            + "echo '   System.InvalidOperationException: Unable to resolve service for type IExampleDispatcher'; "
                            + "i=1; while [ \"$i\" -le 17 ]; do echo \"  Skipped X.Y.SkippedTest$i [< 1 ms]\"; i=$((i+1)); done; "
                            + "i=0; while [ \"$i\" -lt 200 ]; do echo \"progress line $i\"; i=$((i+1)); done; "
                            + "echo 'Failed!  - Failed: 2, Passed: 10, Skipped: 17, Total: 29'; "
                            + "echo '  Failed X.Y.StderrTest [2 ms]' >&2; echo '  Error Message:' >&2; "
                            + "echo '   Assert.Equal() Failure: Values differ' >&2; "
                            + "i=0; while [ \"$i\" -lt 200 ]; do echo \"stderr progress line $i\" >&2; i=$((i+1)); done; exit 1";
                        await EnsureVesselWithProfileAsync(testDb, "ten_digest", "vsl_digest",
                            worktreePath, SuccessCommand(), command).ConfigureAwait(false);

                        DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                            new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                            testDb.Driver,
                            logging);
                        DefinitionOfDoneResult result = await gate.EvaluateAsync(
                            CreateWorkerMission("ten_digest", "vsl_digest"),
                            new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                        string text = result.OutputTail ?? String.Empty;
                        AssertFalse(result.Passed, "The synthetic test run fails");
                        AssertContains("X.Y.EarlyTest: System.InvalidOperationException: Unable to resolve service for type IExampleDispatcher", text,
                            "The early failing test is named with its first exception line");
                        AssertContains("X.Y.StderrTest: Assert.Equal() Failure: Values differ", text,
                            "The failing test printed on stderr is named with its first exception line");
                        for (int i = 1; i <= 17; i++)
                            AssertContains("X.Y.SkippedTest" + i + "\n", text + "\n", "Skip " + i + " of 17 is listed");
                    }
                    finally
                    {
                        TryDeleteDirectory(worktreePath);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Gate leaves the failing test set null for a non-test build failure", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_buildnonames", "vsl_buildnonames",
                        worktreePath, CompileErrorCommand(), SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_buildnonames", "vsl_buildnonames");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail on a compile error");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.Compile, result.FailureClass!.Value, "The failure is a compile failure");
                    AssertNull(result.FailedTestNames, "A non-test failure carries no failing test set");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A settings reload that turns the gate off or on reaches the running gate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings live = new ArmadaSettings();
                    live.DefinitionOfDone.Enabled = true;
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(live.DefinitionOfDone, testDb.Driver, CreateLogging());
                    Mission mission = CreateWorkerMission("ten_reload", "vsl_reload");
                    Dock dock = new Dock();

                    ArmadaSettings disabled = new ArmadaSettings();
                    disabled.DefinitionOfDone.Enabled = false;
                    live.ApplyHotReloadableFrom(disabled);
                    DefinitionOfDoneResult off = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                    AssertEqual("DoD gate is disabled", off.SkippedReason, "A reload that disables the gate must skip it without a restart.");

                    ArmadaSettings enabled = new ArmadaSettings();
                    enabled.DefinitionOfDone.Enabled = true;
                    live.ApplyHotReloadableFrom(enabled);
                    DefinitionOfDoneResult on = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);
                    AssertNull(on.SkippedReason, "A reload that enables the gate must make it evaluate again.");
                    AssertEqual("dock-setup", on.CommandLabel, "The enabled gate evaluates the mission and reports the dock without a worktree.");
                }
            }).ConfigureAwait(false);

            await RunTest("Gate fails with actionable message when commands are missing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_nocmd", "vsl_nocmd",
                        worktreePath, null, null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_nocmd", "vsl_nocmd");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when no commands are configured");
                    AssertEqual("missing-commands", result.CommandLabel, "CommandLabel should be missing-commands");
                    AssertNotNull(result.OutputTail, "OutputTail should contain an actionable message");
                    AssertContains("workflow profile", result.OutputTail!, "Message should mention workflow profile");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate skips when no workflow profile exists for vessel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselOnlyAsync(testDb, "ten_noprofile", "vsl_noprofile", worktreePath).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_noprofile", "vsl_noprofile");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when no profile exists (no commands)");
                    AssertEqual("missing-commands", result.CommandLabel, "CommandLabel should be missing-commands");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate skips when mission description contains doc-only marker", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_doconly", "vsl_doconly",
                        worktreePath, FailCommand(), FailCommand()).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);

                    Mission mission = CreateWorkerMission("ten_doconly", "vsl_doconly");
                    mission.Description = "This is a documentation-only mission. " + settings.DocOnlyMarker;
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should be skipped (pass) for doc-only missions");
                    AssertNotNull(result.SkippedReason, "SkippedReason should be set for doc-only missions");
                    AssertContains("doc-only", result.SkippedReason!, "SkippedReason should mention doc-only marker");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate skips for Judge persona (Worker-only by default)", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_judge", "vsl_judge",
                        worktreePath, FailCommand(), FailCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateMissionWithPersona("ten_judge", "vsl_judge", "Judge");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should be skipped (pass) for Judge persona");
                    AssertNotNull(result.SkippedReason, "SkippedReason should be set for non-Worker persona");
                    AssertContains("Judge", result.SkippedReason!, "SkippedReason should name the non-applicable persona");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate skips for TestEngineer persona (Worker-only by default)", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_te", "vsl_te",
                        worktreePath, FailCommand(), FailCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateMissionWithPersona("ten_te", "vsl_te", "TestEngineer");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should be skipped (pass) for TestEngineer persona");
                    AssertNotNull(result.SkippedReason, "SkippedReason should be set for TestEngineer");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate skips entirely when Enabled is false", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_disabled", "vsl_disabled",
                        worktreePath, FailCommand(), FailCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_disabled", "vsl_disabled");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Disabled gate should always pass");
                    AssertNotNull(result.SkippedReason, "SkippedReason should indicate gate is disabled");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate failure reason contains command label and exit code", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_reason", "vsl_reason",
                        worktreePath, FailCommand(), null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_reason", "vsl_reason");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail");
                    AssertEqual("build", result.CommandLabel, "CommandLabel should be 'build'");
                    AssertTrue(result.ExitCode != 0, "ExitCode should be non-zero");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate fails with timeout result when a command hangs (rescue: timeout actually fires)", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_timeout", "vsl_timeout",
                        worktreePath, HangCommand(), SuccessCommand()).ConfigureAwait(false);

                    // CommandTimeoutSeconds has a 30-second floor, and honoring it here made this
                    // the slowest test in the suite. The behavior under test is that the timeout
                    // interrupts a hanging read instead of deadlocking -- which a one-second
                    // timeout proves just as well. The production floor is untouched.
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1));

                    Mission mission = CreateWorkerMission("ten_timeout", "vsl_timeout");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when a command exceeds the timeout");
                    AssertEqual("build", result.CommandLabel, "Timed-out command should keep its label");
                    AssertEqual(-1, result.ExitCode, "Timeout failure should report exit code -1");
                    AssertNotNull(result.OutputTail, "Timeout failure should carry a message");
                    AssertContains("timed out", result.OutputTail!, "Message should explain the timeout");
                    if (OperatingSystem.IsLinux())
                    {
                        // The limit counts what the command does after it starts, so the message names what was still
                        // running when it fired; a lock the command took itself would show here the same way.
                        AssertContains("Still running at the limit:", result.OutputTail!, "the message names the processes still running at the limit");
                        AssertContains("sleep 999", result.OutputTail!, "the hanging process is named");
                    }
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A process group snapshot reads the group after a command name that holds spaces and parentheses", () =>
            {
                AssertEqual(4242, ProcessGroupSnapshot.ParseProcessGroup("4250 (sleep) S 4249 4242 4242 0 -1 4194304"), "the third field after the name is the group");
                AssertEqual(77, ProcessGroupSnapshot.ParseProcessGroup("80 (my (odd) name) S 79 77 77 0"), "a name with spaces and parentheses is skipped whole");
                AssertNull(ProcessGroupSnapshot.ParseProcessGroup("80 (truncated"), "text without a closing parenthesis is unreadable");
                AssertNull(ProcessGroupSnapshot.ParseProcessGroup(""), "empty text is unreadable");
                AssertEqual(0, ProcessGroupSnapshot.Read(0).Count, "no group reads as empty");
            }).ConfigureAwait(false);

            await RunTest("A gate that times out while the host is overloaded runs once more after load falls and records the re-run", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_load_rerun", "vsl_load_rerun",
                        worktreePath, HangOnceCommand(), SuccessCommand()).ConfigureAwait(false);

                    // 4 cores: overloaded above 8, fallen at or below 4. The gate starts at load 3; the reading at
                    // the timeout is 20; the wait reads 10, then 6 after one recheck interval, then 3 after the second.
                    FakeHostLoad load = new FakeHostLoad(4, 3.0, 20.0, 10.0, 6.0, 3.0);
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1),
                        load.Monitor);

                    Mission mission = CreateWorkerMission("ten_load_rerun", "vsl_load_rerun");
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "the re-run after load fell passes, and its result stands");
                    AssertEqual(2, CountCommandRuns(worktreePath), "the gate ran its build twice: the timed-out run and the re-run");
                    AssertEqual(2, load.Delays.Count, "the gate waited two recheck intervals for load to fall, without a real wait");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one load re-run event is recorded");
                    AssertEqual(mission.Id, events[0].MissionId, "the event names the mission");
                    DefinitionOfDoneLoadRerunRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadRerunRecord>(events[0].Payload!);
                    AssertNotNull(record, "the event carries the re-run record");
                    AssertEqual("build", record!.TimedOutCommandLabel, "the record names the command that timed out");
                    AssertEqual(20.0, record.LoadAtTimeout, "the record carries the load reading that triggered the re-run");
                    AssertEqual(3.0, record.LoadAtRerun, "the record carries the load reading when the re-run started");
                    AssertEqual(4, record.LogicalCores, "the record carries the core count");
                    AssertEqual(8.0, record.OverloadThreshold, "overloaded means above twice the core count");
                    AssertTrue(record.LoadFell, "load fell before the re-run");
                    AssertEqual(60.0, record.WaitedSeconds, "two 30-second rechecks were waited on the injected clock");

                    if (!OperatingSystem.IsWindows())
                    {
                        string boundedPath = Path.Combine(worktreePath, "bounded-load-retry");
                        Directory.CreateDirectory(boundedPath);
                        await EnsureVesselWithProfileAsync(testDb, "ten_load_budget", "vsl_load_budget",
                            boundedPath, HangOnceCommand(), null, vesselName: "Explicit Load Budget Vessel",
                            gateCommandTimeoutSeconds: 30).ConfigureAwait(false);
                        FakeHostLoad boundedLoad = new FakeHostLoad(4, 3.0, 20.0, 3.0);
                        FastTimeoutGate boundedGate = new FastTimeoutGate(
                            new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 900 },
                            testDb.Driver, logging, TimeSpan.FromSeconds(1), boundedLoad.Monitor);
                        DefinitionOfDoneResult bounded = await boundedGate.EvaluateAsync(
                            CreateWorkerMission("ten_load_budget", "vsl_load_budget"),
                            new Dock { WorktreePath = boundedPath }).ConfigureAwait(false);
                        AssertFalse(bounded.Passed, "Host-load recovery cannot reset an explicit profile's exhausted execution budget.");
                        AssertTrue(bounded.HostLoadTimeout, "The exhausted budget retains measured host-load context without starting a fresh attempt.");
                        AssertEqual(1, CountCommandRuns(boundedPath), "The exhausted command must not start again after load falls.");
                        AssertTrue(File.Exists(Path.Combine(boundedPath, "hang.marker")), "The original command actually started before its timeout.");
                    }
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate that times out again on its load re-run fails with the Timeout result after the bounded wait", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_load_twice", "vsl_load_twice",
                        worktreePath, CountedHangCommand(), SuccessCommand()).ConfigureAwait(false);

                    // The gate starts at load 3. Load then never falls, so the wait ends at its maximum and the
                    // re-run proceeds anyway.
                    FakeHostLoad load = new FakeHostLoad(4, 3.0, 20.0);
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1),
                        load.Monitor);

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_load_twice", "vsl_load_twice"), new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "a second timeout fails the gate");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, result.FailureClass, "the failure keeps its Timeout class");
                    AssertEqual("build", result.CommandLabel, "the failure keeps its command label");
                    AssertContains("timed out", result.OutputTail!, "the failure keeps the timeout message");
                    AssertEqual(2, CountCommandRuns(worktreePath), "the gate re-runs once, never twice");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one load re-run event is recorded");
                    DefinitionOfDoneLoadRerunRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadRerunRecord>(events[0].Payload!);
                    AssertFalse(record!.LoadFell, "the record says load did not fall");
                    AssertEqual(HostLoadMonitor.MaximumWait.TotalSeconds, record.WaitedSeconds, "the wait ended at its maximum");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate that times out at normal or unknown host load fails at once without a re-run", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                List<string> worktrees = new List<string>();
                try
                {
                    // 8.0 on 4 cores is exactly twice the core count, which is not above it. Null is a host
                    // that reports no load.
                    double?[] readings = new double?[] { 8.0, null };
                    for (int i = 0; i < readings.Length; i++)
                    {
                        string worktreePath = CreateTempDir();
                        worktrees.Add(worktreePath);
                        string tenantId = "ten_load_normal_" + i;
                        string vesselId = "vsl_load_normal_" + i;
                        await EnsureVesselWithProfileAsync(testDb, tenantId, vesselId,
                            worktreePath, CountedHangCommand(), SuccessCommand(),
                            vesselName: "Load Normal Vessel " + i).ConfigureAwait(false);

                        FakeHostLoad load = new FakeHostLoad(4, readings[i]);
                        DefinitionOfDoneGate gate = new FastTimeoutGate(
                            new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                            testDb.Driver,
                            logging,
                            TimeSpan.FromSeconds(1),
                            load.Monitor);

                        DefinitionOfDoneResult result = await gate.EvaluateAsync(
                            CreateWorkerMission(tenantId, vesselId), new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                        string at = "load " + (readings[i]?.ToString() ?? "unknown");
                        AssertFalse(result.Passed, "the timeout fails the gate at " + at);
                        AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, result.FailureClass, "the failure is classified Timeout at " + at);
                        AssertEqual(1, CountCommandRuns(worktreePath), "the build ran once at " + at);
                        AssertEqual(0, load.Delays.Count, "the gate did not wait at " + at);
                    }

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(0, events.Count, "no load re-run is recorded");
                }
                finally
                {
                    foreach (string worktreePath in worktrees) TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate whose command times out while another command holds a host slot runs once more with the host to itself", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                int slotsBefore = HostWideCommandLock.SlotCount;
                IDisposable? otherCommand = null;
                Task? releaseOther = null;
                try
                {
                    // The other command holds a slot and a lock the build also takes (busy.marker stands for a vessel
                    // suite lock): while it holds them, the build waits out its time limit. A re-run that starts while
                    // the other command still holds its slot meets the same lock and times out again.
                    await EnsureVesselWithProfileAsync(testDb, "ten_slot_shared", "vsl_slot_shared",
                        worktreePath, SharedLockCommand(), SuccessCommand()).ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(worktreePath, "busy.marker"), "held");

                    // Two slots, normal load throughout, so only the shared slot can explain the time limit.
                    HostWideCommandLock.SetSlotCount(2);
                    otherCommand = await HostWideCommandLock.AcquireAsync("chk_slot_shared", "another vessel's suite", CancellationToken.None).ConfigureAwait(false);
                    FakeHostLoad load = new FakeHostLoad(4, 3.0);
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1),
                        load.Monitor);

                    Mission mission = CreateWorkerMission("ten_slot_shared", "vsl_slot_shared");

                    // The other command finishes, and drops its lock, only once the gate's re-run waits for the host.
                    // A re-run that never waits finds the lock still held.
                    IDisposable? heldByOther = otherCommand;
                    releaseOther = Task.Run(async () =>
                    {
                        DateTime giveUp = DateTime.UtcNow.AddSeconds(20);
                        while (DateTime.UtcNow < giveUp)
                        {
                            if (HostWideCommandLock.Snapshot().Waiters.Any(waiter => waiter.Key == mission.Id)) break;
                            await Task.Delay(20).ConfigureAwait(false);
                        }

                        File.Delete(Path.Combine(worktreePath, "busy.marker"));
                        heldByOther!.Dispose();
                    });

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
                    await releaseOther.ConfigureAwait(false);

                    AssertTrue(result.Passed, "the re-run waits for the host to itself, passes, and its result stands; got " + result.FailureClass + ": " + result.OutputTail);
                    AssertEqual(2, CountCommandRuns(worktreePath), "the gate ran its build twice: the timed-out run and the re-run");
                    AssertEqual(0, load.Delays.Count, "the gate does not wait for load at normal load");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one re-run event is recorded");
                    DefinitionOfDoneLoadRerunRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadRerunRecord>(events[0].Payload!);
                    AssertEqual(DefinitionOfDoneLoadRerunRecord.HostSlotSharedReason, record!.Reason, "the record names the shared host slot as the reason");
                    AssertTrue(record.SharedHostSlot, "the record says the timed-out command shared the host");
                    AssertTrue(record.RerunAlone, "the record says the re-run holds the host alone");
                    AssertEqual("build", record.TimedOutCommandLabel, "the record names the command that timed out");
                    AssertEqual(0.0, record.WaitedSeconds, "the re-run did not wait for load");
                }
                finally
                {
                    if (releaseOther != null) await releaseOther.ConfigureAwait(false);
                    otherCommand?.Dispose();
                    HostWideCommandLock.SetSlotCount(slotsBefore);
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate whose command is still writing output at its time limit runs once more and reports a second limit as host load", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // Normal load, no other command on the host: only the live output says the command was working.
                    await EnsureVesselWithProfileAsync(testDb, "ten_live_output", "vsl_live_output",
                        worktreePath, LiveOutputCommand(), SuccessCommand()).ConfigureAwait(false);
                    FakeHostLoad load = new FakeHostLoad(4, 3.0);
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1),
                        load.Monitor);

                    Mission mission = CreateWorkerMission("ten_live_output", "vsl_live_output");
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "a command that never finishes fails the gate");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, result.FailureClass, "the failure keeps its Timeout class");
                    AssertEqual(2, CountCommandRuns(worktreePath), "the gate re-runs a command still writing output once, never twice");
                    AssertTrue(result.TimedOutWhileProgressing, "the re-run's time limit also caught the command writing output");
                    AssertTrue(result.HostLoadTimeout, "the second time limit reads as host load, so recovery may rescue it once");
                    AssertContains(DefinitionOfDoneFailureClassifier.HostLoadTimeoutMarker, MissionService.BuildDodFailureReason(result).Split('\n')[0],
                        "the recorded reason's first line names the host-load time limit");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one re-run event is recorded");
                    DefinitionOfDoneLoadRerunRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadRerunRecord>(events[0].Payload!);
                    AssertEqual(DefinitionOfDoneLoadRerunRecord.OutputProgressingReason, record!.Reason, "the record names the live output as the reason");
                    AssertTrue(record.OutputProgressing, "the record says the command was writing output");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A workflow profile's gate command time limit applies to its vessel commands, and the global limit applies without one", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string withLimit = CreateTempDir();
                string withoutLimit = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_profile_limit", "vsl_profile_limit",
                        withLimit, SuccessCommand(), SuccessCommand(), vesselName: "Profile Limit Vessel",
                        gateCommandTimeoutSeconds: 2400).ConfigureAwait(false);
                    await EnsureVesselWithProfileAsync(testDb, "ten_global_limit", "vsl_global_limit",
                        withoutLimit, SuccessCommand(), SuccessCommand(), vesselName: "Global Limit Vessel").ConfigureAwait(false);
                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 900 };

                    FastTimeoutGate profileGate = new FastTimeoutGate(settings, testDb.Driver, logging, TimeSpan.FromSeconds(30));
                    Mission profileMission = CreateWorkerMission("ten_profile_limit", "vsl_profile_limit");
                    DefinitionOfDoneResult profileResult = await profileGate.EvaluateAsync(profileMission, new Dock { WorktreePath = withLimit }).ConfigureAwait(false);
                    AssertTrue(profileResult.Passed, "the profile vessel's gate passes");
                    AssertEqual("2400,2400", String.Join(",", profileGate.ConfiguredSeconds), "the build and the unit tests run under the profile's limit");
                    DefinitionOfDoneConfiguration profileConfig = await profileGate.DescribeAsync(profileMission).ConfigureAwait(false);
                    AssertEqual(2400, profileConfig.CommandTimeoutSeconds, "the effective configuration reports the profile's limit");
                    AssertEqual(DefinitionOfDoneGate.CommandTimeoutProfileSource, profileConfig.CommandTimeoutSource, "the limit is named as the profile's");

                    FastTimeoutGate globalGate = new FastTimeoutGate(settings, testDb.Driver, logging, TimeSpan.FromSeconds(30));
                    Mission globalMission = CreateWorkerMission("ten_global_limit", "vsl_global_limit");
                    DefinitionOfDoneResult globalResult = await globalGate.EvaluateAsync(globalMission, new Dock { WorktreePath = withoutLimit }).ConfigureAwait(false);
                    AssertTrue(globalResult.Passed, "the other vessel's gate passes");
                    AssertEqual("900,900", String.Join(",", globalGate.ConfiguredSeconds), "a profile without a limit runs under the global limit");
                    DefinitionOfDoneConfiguration globalConfig = await globalGate.DescribeAsync(globalMission).ConfigureAwait(false);
                    AssertEqual(900, globalConfig.CommandTimeoutSeconds, "the effective configuration reports the global limit");
                    AssertEqual(DefinitionOfDoneGate.CommandTimeoutSettingsSource, globalConfig.CommandTimeoutSource, "the limit is named as the settings'");

                    if (!OperatingSystem.IsWindows())
                    {
                        string retryPath = Path.Combine(withLimit, "aggregate-retry");
                        Directory.CreateDirectory(retryPath);
                        const string failingName = "Sample.Tests.BudgetFixture.Completes_within_bound";
                        string fullCommand = "sleep 0.1; printf '  Failed " + failingName
                            + " [1 ms]\\n0 passed; 1 failed.\\n'; exit 1";
                        string retryCommand = "touch retry-started; sleep 0.1; exit 0";
                        await EnsureVesselWithProfileAsync(testDb, "ten_retry_budget", "vsl_retry_budget",
                            retryPath, null, fullCommand, vesselName: "Explicit Retry Budget Vessel",
                            isolatedTestRerunCommand: retryCommand, gateCommandTimeoutSeconds: 30).ConfigureAwait(false);
                        DefinitionOfDoneSettings retrySettings = new DefinitionOfDoneSettings
                        {
                            Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 900
                        };
                        FastTimeoutGate fittingGate = new FastTimeoutGate(retrySettings, testDb.Driver, logging, TimeSpan.FromSeconds(1));
                        Mission retryMission = CreateWorkerMission("ten_retry_budget", "vsl_retry_budget");
                        DefinitionOfDoneResult fitting = await fittingGate.EvaluateAsync(retryMission,
                            new Dock { WorktreePath = retryPath }).ConfigureAwait(false);
                        bool fittingEnteredRetry = File.Exists(Path.Combine(retryPath, "retry-started"));
                        File.Delete(Path.Combine(retryPath, "retry-started"));
                        List<WorkflowProfile> retryProfiles = await testDb.Driver.WorkflowProfiles.EnumerateAllAsync(
                            new WorkflowProfileQuery { TenantId = "ten_retry_budget", VesselId = "vsl_retry_budget" }).ConfigureAwait(false);
                        AssertEqual(1, retryProfiles.Count);
                        WorkflowProfile retryProfile = retryProfiles[0];
                        retryProfile.UnitTestCommand = fullCommand.Replace("sleep 0.1", "sleep 0.65", StringComparison.Ordinal);
                        retryProfile.IsolatedTestRerunCommand = retryCommand.Replace("sleep 0.1", "sleep 0.65", StringComparison.Ordinal);
                        await testDb.Driver.WorkflowProfiles.UpdateAsync(retryProfile).ConfigureAwait(false);
                        FastTimeoutGate lateGate = new FastTimeoutGate(retrySettings, testDb.Driver, logging, TimeSpan.FromSeconds(1));
                        DefinitionOfDoneResult late = await lateGate.EvaluateAsync(retryMission,
                            new Dock { WorktreePath = retryPath }).ConfigureAwait(false);
                        AssertTrue(fittingEnteredRetry && File.Exists(Path.Combine(retryPath, "retry-started")),
                            "Both cases must enter retry; a timeout of the first command is not aggregate-budget proof.");
                        AssertTrue(fitting.Passed, "An isolated retry that fits the remaining execution budget may pass.");
                        AssertFalse(late.Passed, "The full command and isolated retry share the explicit execution budget.");
                        AssertTrue(fittingGate.ConfiguredSeconds.Count > 0 && lateGate.ConfiguredSeconds.Count > 0,
                            "The native timeout seam resolved both execution budgets.");
                        foreach (int configured in fittingGate.ConfiguredSeconds)
                            AssertEqual(30, configured, "The fitting native retry uses the profile limit, never the global limit.");
                        foreach (int configured in lateGate.ConfiguredSeconds)
                            AssertEqual(30, configured, "The bounded native retry uses the profile limit, never the global limit.");
                    }
                }
                finally
                {
                    TryDeleteDirectory(withLimit);
                    TryDeleteDirectory(withoutLimit);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate whose command times out at a normal 1-minute load after a run under load runs once more", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_load_long", "vsl_load_long",
                        worktreePath, HangOnceCommand(), SuccessCommand()).ConfigureAwait(false);

                    // 4 cores: overloaded above 8. The 1-minute average reads 3 at the start, 6 when the command
                    // times out (its own processes have exited), and 3 in the wait; the 15-minute average is 12
                    // throughout, so the run itself stood under load.
                    FakeHostLoad load = new FakeHostLoad(4, 3.0, 6.0, 3.0);
                    load.LongReadings = new double?[] { 12.0 };
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1),
                        load.Monitor);

                    Mission mission = CreateWorkerMission("ten_load_long", "vsl_load_long");
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "the re-run passes, and its result stands");
                    AssertEqual(2, CountCommandRuns(worktreePath), "the gate ran its build twice: the timed-out run and the re-run");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadRerunRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one load re-run event is recorded");
                    DefinitionOfDoneLoadRerunRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadRerunRecord>(events[0].Payload!);
                    AssertEqual(6.0, record!.LoadAtTimeout, "the record carries the 1-minute reading at the timeout");
                    AssertEqual(12.0, record.LongLoadAtTimeout, "the record carries the 15-minute reading that showed the run under load");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A test command that names failing tests and then hits its time limit fails as TestFail and records the time limit", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    string namedThenHang = "echo '  Failed Example.Suite.ReadsTheClock [12 ms]'; sleep 999";
                    await EnsureVesselWithProfileAsync(testDb, "ten_timeout_named", "vsl_timeout_named",
                        worktreePath, SuccessCommand(), namedThenHang).ConfigureAwait(false);
                    await EnsureVesselWithProfileAsync(testDb, "ten_timeout_bare", "vsl_timeout_bare",
                        worktreePath, SuccessCommand(), HangCommand(), vesselName: "Bare Timeout Vessel").ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(1));

                    DefinitionOfDoneResult named = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_timeout_named", "vsl_timeout_named"), new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
                    AssertFalse(named.Passed, "the run fails");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, named.FailureClass, "a run that named a failing test is a test failure");
                    AssertTrue(named.TimedOut, "the time limit is recorded as its own fact");
                    AssertNotNull(named.FailedTestNames, "the failing test names are kept");
                    AssertEqual("Example.Suite.ReadsTheClock", named.FailedTestNames![0], "the named test is kept");
                    AssertTrue(named.FailedTestNamesOverflow, "the set is incomplete, because tests after the limit never ran");

                    DefinitionOfDoneEvaluationRecord record = DefinitionOfDoneEvaluationRecord.FromResult(named, DateTime.UtcNow);
                    AssertTrue(record.TimedOut, "the evaluation record carries the time limit");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, record.FailureClass, "the evaluation record carries the test failure");

                    DefinitionOfDoneResult bare = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_timeout_bare", "vsl_timeout_bare"), new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);
                    AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, bare.FailureClass, "a run that named no failing test stays a Timeout");
                    AssertTrue(bare.TimedOut, "the bare timeout records the time limit too");
                    AssertNull(bare.FailedTestNames, "a bare timeout names no tests");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("A gate due to start on an overloaded host waits until it is no longer overloaded and records why", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_load_admit", "vsl_load_admit",
                        worktreePath, SuccessCommand(), SuccessCommand()).ConfigureAwait(false);

                    // 4 cores: overloaded above 8. The gate is due at 20; the wait reads 10 at once, 10 after one
                    // recheck interval and 6 after the second, which is no longer overloaded.
                    FakeHostLoad load = new FakeHostLoad(4, 20.0, 10.0, 10.0, 6.0);
                    DefinitionOfDoneGate gate = new FastTimeoutGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging,
                        TimeSpan.FromSeconds(30),
                        load.Monitor);

                    Mission mission = CreateWorkerMission("ten_load_admit", "vsl_load_admit");
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "the gate runs after the wait and passes");
                    AssertEqual(2, load.Delays.Count, "the gate waited two recheck intervals before its first command");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync(DefinitionOfDoneLoadAdmissionRecord.EventType).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "one load admission event is recorded");
                    AssertEqual(mission.Id, events[0].MissionId, "the event names the mission");
                    DefinitionOfDoneLoadAdmissionRecord? record = JsonSerializer.Deserialize<DefinitionOfDoneLoadAdmissionRecord>(events[0].Payload!);
                    AssertEqual(DefinitionOfDoneLoadAdmissionRecord.HostOverloadedReason, record!.Reason, "the wait names its reason");
                    AssertEqual(20.0, record.LoadAtAdmission, "the record carries the load when the gate was due");
                    AssertEqual(6.0, record.LoadAtStart, "the record carries the load when the gate started");
                    AssertTrue(record.LoadFell, "the host stopped being overloaded before the gate started");
                    AssertEqual(60.0, record.WaitedSeconds, "two 30-second rechecks were waited on the injected clock");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Host load reads the 1-minute and 15-minute averages from /proc/loadavg text and treats an unknown reading as normal load", () =>
            {
                AssertEqual(17.25, HostLoadMonitor.ParseLoadAverage("17.25 9.10 4.00 3/912 40211\n"), "the first field is the 1-minute average");
                AssertNull(HostLoadMonitor.ParseLoadAverage(""), "empty text is an unknown reading");
                AssertNull(HostLoadMonitor.ParseLoadAverage("not-a-number 1 2"), "unparseable text is an unknown reading");

                HostLoadMonitor monitor = new HostLoadMonitor(() => null, () => 4);
                HostLoadReading unknown = monitor.Read();
                AssertFalse(monitor.IsOverloaded(unknown), "an unknown reading is never overloaded");
                AssertTrue(monitor.HasFallen(unknown), "an unknown reading counts as fallen");
                AssertTrue(monitor.IsOverloaded(new HostLoadReading { LoadAverage1Min = 8.01, LogicalCores = 4 }), "above twice the cores is overloaded");
                AssertFalse(monitor.HasFallen(new HostLoadReading { LoadAverage1Min = 4.01, LogicalCores = 4 }), "above the core count has not fallen");
                AssertTrue(monitor.HasFallen(new HostLoadReading { LoadAverage1Min = 4.0, LogicalCores = 4 }), "at the core count has fallen");
                AssertEqual(4.00, HostLoadMonitor.ParseLongLoadAverage("17.25 9.10 4.00 3/912 40211\n"), "the third field is the 15-minute average");
                AssertNull(HostLoadMonitor.ParseLongLoadAverage("17.25"), "text without a third field is an unknown 15-minute reading");
                AssertNull(unknown.LoadAverage15Min, "a monitor given its own 1-minute source reads the 15-minute average as unknown");
                AssertEqual(9.10, HostLoadMonitor.ParseMidLoadAverage("17.25 9.10 4.00 3/912 40211\n"), "the second field is the 5-minute average");
                AssertNull(HostLoadMonitor.ParseMidLoadAverage("17.25"), "text without a second field is an unknown 5-minute reading");
                AssertNull(unknown.LoadAverage5Min, "a monitor given its own 1-minute source reads the 5-minute average as unknown");
                AssertNull(unknown.AvailableMemoryMb, "a monitor given its own 1-minute source reads available memory as unknown");
                AssertEqual(2048L, new HostLoadMonitor(() => null, () => 4, memoryReader: () => 2048).Read().AvailableMemoryMb, "a supplied memory reader is read");
                AssertTrue(monitor.RanOverloaded(new HostLoadReading { LoadAverage1Min = 3.0, LoadAverage15Min = 8.01, LogicalCores = 4 }), "a 15-minute average above twice the cores means the run stood under load");
                AssertFalse(monitor.RanOverloaded(new HostLoadReading { LoadAverage1Min = 3.0, LoadAverage15Min = 8.0, LogicalCores = 4 }), "neither average above twice the cores is a run at normal load");
            }).ConfigureAwait(false);

            await RunTest("Gate redacts secret-like lines in the failing-command output tail", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_redact", "vsl_redact",
                        worktreePath, FailWithSecretCommand("hunter2"), null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_redact", "vsl_redact");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail on the failing command");
                    AssertNotNull(result.OutputTail, "Failing command should carry an output tail");
                    AssertContains("[REDACTED]", result.OutputTail!, "Secret-like value should be redacted");
                    AssertFalse(result.OutputTail!.Contains("hunter2"), "Raw secret must not leak into the output tail");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate preserves a citation offender located between selected diagnostics and the output tail", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    string worktreePath = CreateTempDir();
                    try
                    {
                        string outputCommand = "i=0; while [ \"$i\" -lt 1000 ]; do "
                            + "if [ \"$i\" -eq 500 ]; then echo 'CITATION OFFENDER: ExampleComponent lacks required source citation'; "
                            + "else echo \"error CS1000: synthetic diagnostic $i\"; fi; i=$((i+1)); done; exit 1";
                        await EnsureVesselWithProfileAsync(testDb, "ten_capture_output", "vsl_captout",
                            worktreePath, SuccessCommand(), outputCommand).ConfigureAwait(false);

                        DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                            new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                            testDb.Driver,
                            logging);
                        DefinitionOfDoneResult result = await gate.EvaluateAsync(
                            CreateWorkerMission("ten_capture_output", "vsl_captout"),
                            new Dock { WorktreePath = worktreePath }).ConfigureAwait(false);

                        AssertFalse(result.Passed, "The synthetic command fails");
                        AssertFalse((result.OutputTail ?? String.Empty).Contains("CITATION OFFENDER", StringComparison.Ordinal),
                            "The selected diagnostics and tail omit the middle offender");
                        string? capturedOutput = typeof(DefinitionOfDoneResult).GetProperty("GateCommandOutput")
                            ?.GetValue(result) as string;
                        AssertContains("CITATION OFFENDER: ExampleComponent", capturedOutput ?? String.Empty,
                            "The full output retained by the bounded runner preserves the omitted middle offender");
                        bool? runnerOutputTruncated = typeof(DefinitionOfDoneResult).GetProperty("RunnerOutputTruncated")
                            ?.GetValue(result) as bool?;
                        AssertFalse(runnerOutputTruncated ?? true, "This synthetic output stays below the runner's per-stream cap");
                    }
                    finally
                    {
                        TryDeleteDirectory(worktreePath);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Gate fails with dock-setup when the dock has no worktree path", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_nodock", "vsl_nodock",
                        worktreePath, SuccessCommand(), SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_nodock", "vsl_nodock");
                    Dock dock = new Dock { WorktreePath = null };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when the dock lacks a worktree path");
                    AssertEqual("dock-setup", result.CommandLabel, "CommandLabel should identify the dock-setup failure");
                    AssertEqual(-1, result.ExitCode, "Dock-setup failure should report exit code -1");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Harbor-owned gate does not fall back to an Admiral-local working directory", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string localDirectory = CreateTempDir();
                string localMarker = Path.Combine(localDirectory, "local-gate-ran.txt");
                try
                {
                    string command = OperatingSystem.IsWindows()
                        ? "echo LOCAL_GATE_RAN>" + localMarker
                        : "printf LOCAL_GATE_RAN > '" + localMarker + "'";
                    await EnsureVesselWithProfileAsync(testDb, "ten_harbor_gate", "vsl_harbor_gate",
                        localDirectory, command, command).ConfigureAwait(false);
                    HarborSettings harborSettings = new HarborSettings
                    {
                        Enabled = true,
                        MissionRoutes = new List<HarborMissionRoute>
                        {
                            new HarborMissionRoute
                            {
                                VesselId = "vsl_harbor_gate",
                                RunnerId = "runner-harbor-gate",
                                RepositoryGrantId = "grant-harbor-gate",
                                CheckoutMode = HarborCheckoutModeEnum.HarborOwned
                            }
                        }
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver, logging)
                    {
                        HarborSettings = harborSettings
                    };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        CreateWorkerMission("ten_harbor_gate", "vsl_harbor_gate"),
                        new Dock { WorktreePath = "harbor:/dock/missing" }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "an owned route without its bound remote checkout cannot pass through local commands");
                    AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, result.FailureClass, "missing remote ownership is an infrastructure failure");
                    AssertContains("harbor_owned_gate_binding_missing", result.OutputTail ?? String.Empty,
                        "the refusal gives the operator a stable reason");
                    AssertFalse(File.Exists(localMarker), "the local profile command never runs for an owned route");
                    await AssertHarborExecutionBudgetAsync().ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteDirectory(localDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate runs (does not skip) for a persona explicitly listed in AppliedPersonas", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_applied", "vsl_applied",
                        worktreePath, SuccessCommand(), SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, AppliedPersonas = new List<string> { "Worker", "Judge" } },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateMissionWithPersona("ten_applied", "vsl_applied", "Judge");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should pass when commands succeed for an applied persona");
                    AssertNull(result.SkippedReason, "Gate should actually run (not skip) for an applied persona");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Settings clamp CommandTimeoutSeconds, OutputTailLines, and normalize DocOnlyMarker", async () =>
            {
                AssertEqual(30, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 5 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds below floor should clamp to 30");
                AssertEqual(3600, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 99999 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds above ceiling should clamp to 3600");

                AssertEqual(10, new DefinitionOfDoneSettings { OutputTailLines = 1 }.OutputTailLines,
                    "OutputTailLines below floor should clamp to 10");
                AssertEqual(500, new DefinitionOfDoneSettings { OutputTailLines = 99999 }.OutputTailLines,
                    "OutputTailLines above ceiling should clamp to 500");

                AssertEqual("[DOD:DOC-ONLY]", new DefinitionOfDoneSettings { DocOnlyMarker = "   " }.DocOnlyMarker,
                    "Whitespace DocOnlyMarker should fall back to the default");
                AssertEqual("[CUSTOM]", new DefinitionOfDoneSettings { DocOnlyMarker = "  [CUSTOM]  " }.DocOnlyMarker,
                    "DocOnlyMarker should be trimmed");

                await Task.CompletedTask.ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("RunRestoreBeforeBuild strips --no-restore from build command before execution", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // Build command writes its own text into echo_output.txt; with --no-restore in
                    // the profile command but RunRestoreBeforeBuild=true, the gate must strip the
                    // token before running so the file content does NOT contain "--no-restore".
                    string buildCmd = EchoToFileCommand("echo_output.txt", "build_args --no-restore");
                    await EnsureVesselWithProfileAsync(testDb, "ten_strip_build", "vsl_strip_build",
                        worktreePath, buildCmd, null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = true },
                        testDb.Driver,
                        logging);
                    Mission mission = CreateWorkerMission("ten_strip_build", "vsl_strip_build");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass after stripping --no-restore from the build command");
                    string outputPath = Path.Combine(worktreePath, "echo_output.txt");
                    AssertTrue(File.Exists(outputPath), "Build command must have run and created the output file");
                    string fileContent = File.ReadAllText(outputPath);
                    AssertFalse(fileContent.Contains("--no-restore"),
                        "--no-restore must be stripped from the effective build command before execution");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("RunRestoreBeforeBuild strips --no-restore from test command before execution", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // Build succeeds normally; test command contains --no-restore and must have it stripped.
                    string testCmd = EchoToFileCommand("test_output.txt", "test_args --no-restore");
                    await EnsureVesselWithProfileAsync(testDb, "ten_strip_test", "vsl_strip_test",
                        worktreePath, SuccessCommand(), testCmd).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = true },
                        testDb.Driver,
                        logging);
                    Mission mission = CreateWorkerMission("ten_strip_test", "vsl_strip_test");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass after stripping --no-restore from the test command");
                    string outputPath = Path.Combine(worktreePath, "test_output.txt");
                    AssertTrue(File.Exists(outputPath), "Test command must have run and created the output file");
                    string fileContent = File.ReadAllText(outputPath);
                    AssertFalse(fileContent.Contains("--no-restore"),
                        "--no-restore must be stripped from the effective test command before execution");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("--no-restore is preserved in build command when RunRestoreBeforeBuild is false", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // RunRestoreBeforeBuild=false: the gate must NOT strip --no-restore; the output
                    // file must still contain the token.
                    string buildCmd = EchoToFileCommand("echo_output.txt", "build_args --no-restore");
                    await EnsureVesselWithProfileAsync(testDb, "ten_nostrip", "vsl_nostrip",
                        worktreePath, buildCmd, null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);
                    Mission mission = CreateWorkerMission("ten_nostrip", "vsl_nostrip");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass (build command itself exits 0)");
                    string outputPath = Path.Combine(worktreePath, "echo_output.txt");
                    AssertTrue(File.Exists(outputPath), "Build command must have run and created the output file");
                    string fileContent = File.ReadAllText(outputPath);
                    AssertTrue(fileContent.Contains("--no-restore"),
                        "--no-restore must be preserved in the build command when RunRestoreBeforeBuild is false");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Build command without --no-restore is unchanged by EnsureRestore (regression guard)", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // A plain build command (no --no-restore) must work normally with
                    // RunRestoreBeforeBuild=true. This guards the shared-library case where the
                    // profile uses dotnet build src/X.sln and no --no-restore token is present.
                    string buildCmd = EchoToFileCommand("echo_output.txt", "build_args");
                    await EnsureVesselWithProfileAsync(testDb, "ten_noflag", "vsl_noflag",
                        worktreePath, buildCmd, null).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = true },
                        testDb.Driver,
                        logging);
                    Mission mission = CreateWorkerMission("ten_noflag", "vsl_noflag");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass for a command that never had --no-restore");
                    string outputPath = Path.Combine(worktreePath, "echo_output.txt");
                    AssertTrue(File.Exists(outputPath), "Build command must have run");
                    string fileContent = File.ReadAllText(outputPath);
                    AssertTrue(fileContent.Contains("build_args"), "Build command content must be preserved unchanged");
                    AssertFalse(fileContent.Contains("--no-restore"), "No spurious token should appear in output");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate runs the vessel's containerless test command when no container runtime is available", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // The normal unit-test command FAILS (stands in for container fixtures with no
                    // runtime); the containerless variant succeeds. With the runtime reported down the
                    // gate must select the containerless command and therefore pass.
                    await EnsureVesselWithProfileAsync(testDb, "ten_noctr", "vsl_noctr",
                        worktreePath, SuccessCommand(), FailCommand(),
                        containerlessTestCommand: SuccessCommand()).ConfigureAwait(false);

                    StubContainerRuntimeProbe probe = new StubContainerRuntimeProbe(false);
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging,
                        probe);

                    Mission mission = CreateWorkerMission("ten_noctr", "vsl_noctr");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed,
                        "With no container runtime the gate must run the scoped containerless command and pass");
                    AssertTrue(probe.CallCount > 0, "The gate must actually consult the container-runtime probe");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate keeps the normal test command when a container runtime is available", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // Same profile, but the runtime IS available: the containerless fallback must NOT
                    // be substituted, so the failing container-backed command still fails the gate.
                    // This is what stops the fallback from quietly becoming the default and hiding
                    // real container test failures.
                    await EnsureVesselWithProfileAsync(testDb, "ten_ctr", "vsl_ctr",
                        worktreePath, SuccessCommand(), FailCommand(),
                        containerlessTestCommand: SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging,
                        new StubContainerRuntimeProbe(true));

                    Mission mission = CreateWorkerMission("ten_ctr", "vsl_ctr");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed,
                        "With a working container runtime the real test command must run and its failure must stand");
                    AssertEqual("unit-test", result.CommandLabel,
                        "The unscoped label proves the containerless variant was not substituted");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Gate ignores the container pre-flight when the vessel configures no containerless command", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // No per-vessel containerless command configured: behavior is unchanged from
                    // before the pre-flight existed, and the probe is never consulted.
                    await EnsureVesselWithProfileAsync(testDb, "ten_noopt", "vsl_noopt",
                        worktreePath, SuccessCommand(), SuccessCommand()).ConfigureAwait(false);

                    StubContainerRuntimeProbe probe = new StubContainerRuntimeProbe(false);
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging,
                        probe);

                    Mission mission = CreateWorkerMission("ten_noopt", "vsl_noopt");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should pass normally when both commands succeed");
                    AssertEqual(0, probe.CallCount,
                        "Without a containerless command there is nothing to fall back to, so the probe must not be paid for");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("WorkflowProfile round-trips ContainerlessUnitTestCommand through the database", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_rt", "vsl_rt",
                        worktreePath, SuccessCommand(), "dotnet test All.sln",
                        containerlessTestCommand: "dotnet test All.sln --filter Category!=Container").ConfigureAwait(false);

                    List<WorkflowProfile> profiles = await testDb.Driver.WorkflowProfiles.EnumerateAllAsync(
                        new WorkflowProfileQuery { TenantId = "ten_rt", PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);

                    AssertTrue(profiles.Count > 0, "The profile should be readable back");
                    AssertEqual("dotnet test All.sln --filter Category!=Container", profiles[0].ContainerlessUnitTestCommand,
                        "The per-vessel containerless command must survive a write/read round trip");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Settings defaults: RunRestoreBeforeBuild true, CommandTimeoutSeconds 600, RestoreCommand absent", async () =>
            {
                DefinitionOfDoneSettings defaults = new DefinitionOfDoneSettings();
                AssertTrue(defaults.RunRestoreBeforeBuild, "Default RunRestoreBeforeBuild must be true");
                AssertEqual(600, defaults.CommandTimeoutSeconds, "Default CommandTimeoutSeconds must be 600");

                // Clamp [30,3600] still applies.
                AssertEqual(30, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 5 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds below floor must clamp to 30");
                AssertEqual(3600, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 99999 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds above ceiling must clamp to 3600");

                await Task.CompletedTask.ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Consumer tests run and fail the gate when a producer change reaches a triggering path", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                string hostRoot = CreateTempDir();
                try
                {
                    // Producer builds and tests clean; the consumer builds clean but its suite fails.
                    await EnsureVesselWithProfileAsync(testDb, "ten_ct", "vsl_ct_producer",
                        producerWorktree, "echo PRIMARY-BUILD", "echo PRIMARY-UNIT", null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_ct", "vsl_ct_consumer",
                        "ExampleConsumer", "vsl_ct_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core/Foo.cs" }
                    };

                    string? consumerBuildProof = null;
                    string? consumerTestProof = null;
                    if (!OperatingSystem.IsWindows())
                    {
                        string dependencyRoot = Path.Combine(hostRoot, "dependency");
                        Directory.CreateDirectory(Path.Combine(dependencyRoot, "subdir"));
                        await File.WriteAllTextAsync(Path.Combine(dependencyRoot, "value.txt"), "host-main").ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(hostRoot, "context.txt"), "host-consumer").ConfigureAwait(false);
                        Vessel producer = (await testDb.Driver.Vessels.ReadAsync("vsl_ct_producer").ConfigureAwait(false))!;
                        producer.WorkingDirectory = dependencyRoot;
                        await testDb.Driver.Vessels.UpdateAsync(producer).ConfigureAwait(false);
                        Vessel consumer = (await testDb.Driver.Vessels.ReadAsync("vsl_ct_consumer").ConfigureAwait(false))!;
                        consumer.WorkingDirectory = hostRoot;
                        await testDb.Driver.Vessels.UpdateAsync(consumer).ConfigureAwait(false);
                        WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                        WorkflowProfile profile = (await profiles.ResolveForVesselAsync(
                            AuthContext.Authenticated("ten_ct", "usr_ct", true, true, "UnitTest"), consumer).ConfigureAwait(false))!;
                        consumerBuildProof = Path.Combine(producerWorktree, "consumer-build.txt");
                        consumerTestProof = Path.Combine(producerWorktree, "consumer-test.txt");
                        string unrelated = hostRoot + "-other";
                        profile.EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["CONSUMER_ROOT"] = hostRoot,
                            ["DEPENDENCY_ROOT"] = dependencyRoot,
                            ["DEPENDENCY_SUBDIR"] = Path.Combine(dependencyRoot, "subdir"),
                            ["UNRELATED_ROOT"] = unrelated,
                            ["LITERAL_VALUE"] = "kept literal",
                            ["PATH_LIST_VALUE"] = dependencyRoot + Path.PathSeparator + unrelated
                        };
                        string verify = "test \"$(cat \"$DEPENDENCY_ROOT/value.txt\")\" = candidate"
                            + " && test \"$(cat \"$CONSUMER_ROOT/context.txt\")\" = consumer-candidate"
                            + " && test \"$DEPENDENCY_SUBDIR\" = \"$DEPENDENCY_ROOT/subdir\""
                            + " && test \"$UNRELATED_ROOT\" = '" + unrelated + "'"
                            + " && test \"$LITERAL_VALUE\" = 'kept literal'"
                            + " && test \"$PATH_LIST_VALUE\" = '" + profile.EnvironmentVariables["PATH_LIST_VALUE"] + "'";
                        profile.BuildCommand = verify + " && printf candidate > '" + consumerBuildProof + "'";
                        profile.UnitTestCommand = verify + " && printf candidate > '" + consumerTestProof + "'; echo CONSUMER-FAIL; exit 1";
                        await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                        git.BeforeWorktreeCreate = async () =>
                        {
                            string path = git.WorktreeCalls[git.WorktreeCalls.Count - 1];
                            if (Path.GetFileName(path) == "vsl_ct_producer")
                            {
                                Directory.CreateDirectory(Path.Combine(path, "subdir"));
                                await File.WriteAllTextAsync(Path.Combine(path, "value.txt"), "candidate").ConfigureAwait(false);
                            }
                            else
                            {
                                await File.WriteAllTextAsync(Path.Combine(path, "context.txt"), "consumer-candidate").ConfigureAwait(false);
                            }
                        };
                    }

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_ct", "vsl_ct_producer");
                    mission.BranchName = "feat/ct";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "A failing consumer suite on a triggering wave must fail the gate");
                    AssertEqual("consumer_tests_failed: ExampleConsumer", result.CommandLabel,
                        "The failure must carry the named consumer-tests reason, distinct from a build failure");
                    if (!OperatingSystem.IsWindows())
                    {
                        AssertEqual("candidate", File.ReadAllText(consumerBuildProof!), "consumer build reads the provisioned producer and consumer paths");
                        AssertEqual("candidate", File.ReadAllText(consumerTestProof!), "consumer tests use the same provisioned paths and preserve unrelated environment");
                    }
                    AssertFalse((result.GateCommandOutput ?? String.Empty).Contains("PRIMARY-UNIT", StringComparison.Ordinal),
                        "a consumer failure retains its own output, never the earlier vessel pass");
                    if (!OperatingSystem.IsWindows()) AssertContains("CONSUMER-FAIL", result.GateCommandOutput ?? String.Empty);

                    WorkflowProfile consumerProfile = (await testDb.Driver.WorkflowProfiles.EnumerateAllAsync(
                        new WorkflowProfileQuery { TenantId = "ten_ct", PageSize = 10 }).ConfigureAwait(false))
                        .Single(profile => profile.VesselId == "vsl_ct_consumer");
                    consumerProfile.BuildCommand = "echo CONSUMER-BUILD";
                    consumerProfile.UnitTestCommand = "echo CONSUMER-PASS";
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(consumerProfile).ConfigureAwait(false);
                    DefinitionOfDoneResult passed = await gate.EvaluateAsync(mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);
                    AssertTrue(passed.Passed);
                    AssertContains("PRIMARY-UNIT", passed.GateCommandOutput ?? String.Empty,
                        "successful consumer verification must not discard the actual vessel test footer");
                    AssertFalse((passed.GateCommandOutput ?? String.Empty).Contains("CONSUMER-PASS", StringComparison.Ordinal));
                    AssertNull(passed.CommandLabel);
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                    TryDeleteDirectory(hostRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("A consumer suite whose tests all pass but whose command fails after them is retried once and fails as a post-test step, not a test failure", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                string runLogDir = CreateTempDir();
                try
                {
                    // Every test run reports zero failures; a verification step after the tests then fails.
                    string runLog = Path.Combine(runLogDir, "consumer-runs.log");
                    string consumerTest = "echo run >> '" + runLog + "'; "
                        + "echo 'Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7 - Example.Core.Tests.dll'; "
                        + "echo 'Passed!  - Failed:     0, Passed:    83, Skipped:     1, Total:    84 - Example.Api.Tests.dll'; "
                        + "echo 'example: workspace no longer verifies after command' >&2; exit 125";
                    await EnsureVesselWithProfileAsync(testDb, "ten_pts", "vsl_pts_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_pts", "vsl_pts_consumer",
                        "ExampleConsumer", "vsl_pts_producer", SuccessCommand(), consumerTest, null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core/Foo.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_pts", "vsl_pts_producer");
                    mission.BranchName = "feat/pts";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    string tail = result.OutputTail ?? String.Empty;
                    AssertFalse(result.Passed, "a step that fails on both runs still fails the gate");
                    AssertEqual("PostTestStep", result.FailureClass?.ToString(), "no test failed, so the class is not TestFail. Tail: " + tail);
                    AssertFalse((result.CommandLabel ?? String.Empty).StartsWith("consumer_tests_failed: ", StringComparison.Ordinal),
                        "the label must not say the consumer's tests failed: " + result.CommandLabel);
                    AssertContains("0 tests failed", tail, "the reason says no test failed");
                    AssertContains("example: workspace no longer verifies after command", tail, "the reason names the step that failed");
                    AssertFalse(tail.Contains("failing test names could not be read in full", StringComparison.Ordinal),
                        "there are no failing test names to read. Tail: " + tail);
                    AssertEqual(2, File.ReadAllLines(runLog).Length, "the command is retried exactly once");

                    // A transient post-test failure can recover only inside this consumer's
                    // original execution budget. The producer has no declared budget.
                    List<WorkflowProfile> consumerProfiles = await testDb.Driver.WorkflowProfiles.EnumerateAllAsync(
                        new WorkflowProfileQuery { TenantId = "ten_pts", VesselId = "vsl_pts_consumer" }).ConfigureAwait(false);
                    AssertEqual(1, consumerProfiles.Count);
                    WorkflowProfile consumerProfile = consumerProfiles[0];
                    consumerProfile.GateCommandTimeoutSeconds = 30;
                    Vessel? consumer = await testDb.Driver.Vessels.ReadAsync("vsl_pts_consumer").ConfigureAwait(false);
                    AssertNotNull(consumer);
                    string boundedPath = Path.Combine(runLogDir, "bounded-consumer");
                    Directory.CreateDirectory(boundedPath);
                    string boundedCommand = "if [ -f first-finished ]; then touch retry-started; sleep 0.1; touch retry-finished; exit 0; fi; "
                        + "sleep 0.1; touch first-finished; printf '7 passed; 0 failed.\\n'; exit 125";
                    consumerProfile.UnitTestCommand = boundedCommand;
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(consumerProfile).ConfigureAwait(false);
                    DefinitionOfDoneSettings boundedSettings = new DefinitionOfDoneSettings
                    {
                        Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 900
                    };
                    FastTimeoutGate fittingGate = new FastTimeoutGate(boundedSettings, testDb.Driver, logging, TimeSpan.FromSeconds(1));
                    DefinitionOfDoneResult fitting = await fittingGate.EvaluateConsumerTestsForTestAsync(
                        mission, consumer!, consumerProfile, boundedPath, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(fitting.Passed, "A consumer post-test retry that fits the remaining budget may recover.");
                    AssertTrue(File.Exists(Path.Combine(boundedPath, "retry-finished")), "The fitting control actually finishes its retry.");
                    foreach (string marker in new[] { "first-finished", "retry-started", "retry-finished" })
                        File.Delete(Path.Combine(boundedPath, marker));
                    consumerProfile.UnitTestCommand = boundedCommand.Replace("sleep 0.1", "sleep 0.65", StringComparison.Ordinal);
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(consumerProfile).ConfigureAwait(false);
                    FastTimeoutGate lateGate = new FastTimeoutGate(boundedSettings, testDb.Driver, logging, TimeSpan.FromSeconds(1));
                    DefinitionOfDoneResult late = await lateGate.EvaluateConsumerTestsForTestAsync(
                        mission, consumer!, consumerProfile, boundedPath, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(File.Exists(Path.Combine(boundedPath, "first-finished"))
                        && File.Exists(Path.Combine(boundedPath, "retry-started")),
                        "The full consumer command must finish and its post-test retry must start before the limit.");
                    AssertFalse(late.Passed, "A consumer post-test retry cannot reset the explicit execution budget.");
                    AssertTrue(late.TimedOut, "The second command reaches the remaining execution limit.");
                    AssertFalse(File.Exists(Path.Combine(boundedPath, "retry-finished")), "The over-budget retry is stopped before its successful exit.");
                    AssertTrue(fittingGate.ConfiguredSeconds.Count > 0 && lateGate.ConfiguredSeconds.Count > 0,
                        "Both consumer evaluations resolve their native execution limits.");
                    foreach (int configured in fittingGate.ConfiguredSeconds)
                        AssertEqual(30, configured, "The fitting post-test retry uses the consumer's limit.");
                    foreach (int configured in lateGate.ConfiguredSeconds)
                        AssertEqual(30, configured, "The bounded post-test retry uses the consumer's limit.");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                    TryDeleteDirectory(runLogDir);
                }
            }).ConfigureAwait(false);

            await RunTest("Consumer tests do NOT run when no producer change reaches a triggering path", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    // The consumer suite would fail if run; the gate must pass because the change is
                    // outside every trigger path, proving the suite is bounded to matching waves.
                    await EnsureVesselWithProfileAsync(testDb, "ten_cx", "vsl_cx_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_cx", "vsl_cx_consumer",
                        "ExampleConsumer", "vsl_cx_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "docs/notes.md", "CHANGELOG.md" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_cx", "vsl_cx_producer");
                    mission.BranchName = "feat/cx";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "A non-triggering wave must not run the consumer suite, so the gate passes");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("A test-only producer change under a trigger path does not run the consumer suite", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_cxt", "vsl_cxt_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_cxt", "vsl_cxt_consumer",
                        "ExampleConsumer", "vsl_cxt_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    // The changed path is under the trigger prefix but is a test project file, so the
                    // rule excludes it: a change to tests alone cannot break the consumer's behavior.
                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core.Tests/FooTests.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_cxt", "vsl_cxt_producer");
                    mission.BranchName = "feat/cxt";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "A test-only change must not trigger the consumer suite");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("A later stage that commits production code re-verifies consumers and fails on a consumer break", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    // The producer's own commands fail: a later stage re-verifies consumers only, so a
                    // failure can come only from the consumer suite.
                    await EnsureVesselWithProfileAsync(testDb, "ten_ls", "vsl_ls_producer",
                        producerWorktree, FailCommand(), FailCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_ls", "vsl_ls_consumer",
                        "ExampleConsumer", "vsl_ls_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core/Signer.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateMissionWithPersona("ten_ls", "vsl_ls_producer", "TestEngineer");
                    mission.BranchName = "feat/ls";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }, CancellationToken.None, "start-commit").ConfigureAwait(false);

                    AssertFalse(result.Passed, "A later stage's production commit that breaks a consumer must fail the gate");
                    AssertEqual("consumer_tests_failed: ExampleConsumer", result.CommandLabel,
                        "The failure must name the consumer suite, not the producer's own commands");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("A later stage whose commits change only test files does not re-verify consumers, whatever sits untracked in its dock", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_lt", "vsl_lt_producer",
                        producerWorktree, FailCommand(), FailCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_lt", "vsl_lt_consumer",
                        "ExampleConsumer", "vsl_lt_producer", FailCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        // Untracked files Armada writes into every dock must not count as the stage's change.
                        ChangedFilesSinceResult = new List<string> { "_briefing/memory/01-core.md", "CURSOR.md", "src/ExampleProducer/ExampleProducer.Core/Signer.cs" },
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core.Tests/SignerTests.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateMissionWithPersona("ten_lt", "vsl_lt_producer", "TestEngineer");
                    mission.BranchName = "feat/lt";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }, CancellationToken.None, "start-commit").ConfigureAwait(false);

                    AssertTrue(result.Passed, "A test-only later stage must not run the failing consumer commands");
                    AssertNotNull(result.SkippedReason, "The stage must report why it was not verified");
                    AssertContains("no production file", result.SkippedReason!, "The skip must name the test-only change");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("VerifyConsumersAfterLaterStages=false leaves a later stage skipped", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_lo", "vsl_lo_producer",
                        producerWorktree, FailCommand(), FailCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_lo", "vsl_lo_consumer",
                        "ExampleConsumer", "vsl_lo_producer", FailCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core/Signer.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, VerifyConsumersAfterLaterStages = false },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateMissionWithPersona("ten_lo", "vsl_lo_producer", "TestEngineer");
                    mission.BranchName = "feat/lo";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }, CancellationToken.None, "start-commit").ConfigureAwait(false);

                    AssertTrue(result.Passed, "With the setting off a later stage stays skipped");
                    AssertContains("TestEngineer", result.SkippedReason ?? "", "The skip keeps the persona reason");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("RunConsumerTests=false leaves the build-only consumer step unchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_coff", "vsl_coff_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_coff", "vsl_coff_consumer",
                        "ExampleConsumer", "vsl_coff_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseResult = new List<string> { "src/ExampleProducer/ExampleProducer.Core/Foo.cs" }
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" }, RunConsumerTests = false },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_coff", "vsl_coff_producer");
                    mission.BranchName = "feat/coff";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertTrue(result.Passed, "With consumer tests disabled the gate builds the consumer only, so it passes");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("Consumer tests run when the producer changed-path read reports unavailable", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    // The consumer suite fails if run. An unavailable read must run it rather than pass as no change.
                    await EnsureVesselWithProfileAsync(testDb, "ten_cu", "vsl_cu_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_cu", "vsl_cu_consumer",
                        "ExampleConsumer", "vsl_cu_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ChangedFilePathsAgainstBaseFailure = "injected git failure"
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_cu", "vsl_cu_producer");
                    mission.BranchName = "feat/cu";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "An unreadable producer change may reach any trigger, so the failing consumer suite must run and fail the gate");
                    AssertEqual("consumer_tests_failed: ExampleConsumer", result.CommandLabel,
                        "The consumer suite must have run; an unreadable change is never treated as no change");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);

            await RunTest("Consumer tests run when reading the producer changed paths throws", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string producerWorktree = CreateTempDir();
                try
                {
                    // The git seam throws. The gate must treat the change as unreadable and run the consumer suite.
                    await EnsureVesselWithProfileAsync(testDb, "ten_cth", "vsl_cth_producer",
                        producerWorktree, SuccessCommand(), SuccessCommand(), null, "ExampleProducer").ConfigureAwait(false);
                    await EnsureConsumerWithSiblingAndProfileAsync(testDb, "ten_cth", "vsl_cth_consumer",
                        "ExampleConsumer", "vsl_cth_producer", SuccessCommand(), FailCommand(), null).ConfigureAwait(false);

                    StubGitService git = new StubGitService
                    {
                        CreateWorktreeDirectories = true,
                        ShouldThrowOnChangedFilePathsAgainstBase = true
                    };

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, ConsumerTestTriggerPaths = new List<string> { "src/ExampleProducer/" } },
                        testDb.Driver, logging, null, git);

                    Mission mission = CreateWorkerMission("ten_cth", "vsl_cth_producer");
                    mission.BranchName = "feat/cth";
                    DefinitionOfDoneResult result = await gate.EvaluateAsync(
                        mission, new Dock { WorktreePath = producerWorktree }).ConfigureAwait(false);

                    AssertFalse(result.Passed, "An unreadable producer change may reach any trigger, so the failing consumer suite must run and fail the gate");
                    AssertEqual("consumer_tests_failed: ExampleConsumer", result.CommandLabel,
                        "The consumer suite must have run; an unreadable change is never treated as no change");
                }
                finally
                {
                    TryDeleteDirectory(producerWorktree);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        /// <summary>
        /// Hand-rolled container-runtime probe double returning a fixed verdict.
        /// </summary>
        private sealed class StubContainerRuntimeProbe : Armada.Core.Services.Interfaces.IContainerRuntimeProbe
        {
            internal int CallCount { get; private set; }

            private readonly bool _Available;

            internal StubContainerRuntimeProbe(bool available)
            {
                _Available = available;
            }

            public Task<bool> IsAvailableAsync(string workingDirectory, CancellationToken token = default)
            {
                CallCount++;
                return Task.FromResult(_Available);
            }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_dod_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static string SuccessCommand()
        {
            return OperatingSystem.IsWindows() ? "echo ok" : "echo ok";
        }

        private static string FailCommand()
        {
            return OperatingSystem.IsWindows() ? "exit 1" : "exit 1";
        }

        // A unit-test run that names two failing tests in the dotnet VSTest shape, then exits non-zero.
        // Uses echo per line so the gate's shell wrapper does not have to interpret escape sequences.
        private static string FailingTestNamesCommand()
        {
            if (OperatingSystem.IsWindows())
                return "echo   Failed X.Y.FirstTest [1 ms]& echo   Failed X.Y.SecondTest [2 ms]& echo Failed!  - Failed: 2, Passed: 0, Total: 2& exit 1";
            return "echo '  Failed X.Y.FirstTest [1 ms]'; echo '  Failed X.Y.SecondTest [2 ms]'; echo 'Failed!  - Failed: 2, Passed: 0, Total: 2'; exit 1";
        }

        // A build command that prints a compiler diagnostic and exits non-zero, so the failure
        // classifies as Compile rather than TestFail.
        private static string CompileErrorCommand()
        {
            if (OperatingSystem.IsWindows())
                return "echo Parser.cs(12,5): error CS0103: identifier x not found& exit 1";
            return "echo 'Parser.cs(12,5): error CS0103: identifier x not found'; exit 1";
        }

        /// <summary>
        /// A command that fails if another copy of itself is running concurrently. It refuses to
        /// start when the sentinel already exists, creates it, holds it long enough for a racing
        /// caller to observe, then clears it. Overlap therefore shows up as a failed gate rather
        /// than as a timing measurement that could flake under load.
        /// </summary>
        private static string MutuallyExclusiveCommand(string lockPath)
        {
            if (OperatingSystem.IsWindows())
            {
                return "if exist \"" + lockPath + "\" (exit 1) else (echo held > \"" + lockPath +
                    "\" && timeout /t 2 /nobreak > nul && del \"" + lockPath + "\")";
            }

            // Both gates start together, so a hold of one second is far longer than the gap between their
            // starts and any overlap still finds the sentinel.
            return "if [ -e '" + lockPath + "' ]; then exit 1; fi; touch '" + lockPath +
                "'; sleep 1; rm -f '" + lockPath + "'";
        }

        /// <summary>
        /// Returns a shell command that echoes the given text into a file in the working
        /// directory. Used by stripping tests to capture the effective command content.
        /// </summary>
        private static string EchoToFileCommand(string fileName, string text)
        {
            return OperatingSystem.IsWindows()
                ? "echo " + text + " > " + fileName
                : "echo '" + text + "' > " + fileName;
        }

        /// <summary>
        /// A command that blocks far longer than any test timeout, used to exercise the timeout path
        /// without relying on interactive sleep helpers.
        /// </summary>
        private static string HangCommand()
        {
            return OperatingSystem.IsWindows() ? "ping -n 999 127.0.0.1 >nul" : "sleep 999";
        }

        /// <summary>
        /// A command that emits a single secret-like line and then exits non-zero, used to verify
        /// that the failing-command output tail is redacted.
        /// </summary>
        private static string FailWithSecretCommand(string secret)
        {
            return OperatingSystem.IsWindows()
                ? "echo password=" + secret + "& exit 1"
                : "echo password=" + secret + "; exit 1";
        }

        private static Mission CreateWorkerMission(string tenantId, string vesselId)
        {
            return CreateMissionWithPersona(tenantId, vesselId, "Worker");
        }

        private static Mission CreateMissionWithPersona(string tenantId, string vesselId, string persona)
        {
            Mission mission = new Mission("DoD gate test mission")
            {
                TenantId = tenantId,
                VesselId = vesselId,
                Persona = persona
            };
            return mission;
        }

        private static async Task EnsureVesselWithProfileAsync(
            TestDatabase testDb,
            string tenantId,
            string vesselId,
            string worktreePath,
            string? buildCommand,
            string? testCommand,
            string? containerlessTestCommand = null,
            string vesselName = "Test Vessel",
            string? failedTestLinePattern = null,
            string? isolatedTestRerunCommand = null,
            int? gateCommandTimeoutSeconds = null,
            Dictionary<string, string>? environmentVariables = null)
        {
            await EnsureVesselOnlyAsync(testDb, tenantId, vesselId, worktreePath, vesselName).ConfigureAwait(false);

            WorkflowProfile profile = new WorkflowProfile
            {
                TenantId = tenantId,
                Name = "Test Profile",
                Scope = Armada.Core.Enums.WorkflowProfileScopeEnum.Vessel,
                VesselId = vesselId,
                BuildCommand = buildCommand,
                UnitTestCommand = testCommand,
                ContainerlessUnitTestCommand = containerlessTestCommand,
                FailedTestLinePattern = failedTestLinePattern,
                IsolatedTestRerunCommand = isolatedTestRerunCommand,
                GateCommandTimeoutSeconds = gateCommandTimeoutSeconds,
                EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
                IsDefault = true,
                Active = true
            };
            await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
        }

        // An example console runner that prints one "FAIL <sentence>: <message>" line per failing test to
        // stderr. No built-in matcher reads that shape, so only a profile pattern reads the names.
        private const string _ExampleRunnerPattern = "^FAIL (?<name>.+?): ";

        private static string WriteExampleRunner(string worktreePath, params string[] failingSentences)
        {
            System.Text.StringBuilder script = new System.Text.StringBuilder("#!/bin/sh\necho \"ran 12 tests\"\n");
            foreach (string sentence in failingSentences)
                script.Append("printf '%s\\n' \"FAIL ").Append(sentence).Append(": assertion failed\" >&2\n");
            script.Append("exit 1\n");
            File.WriteAllText(Path.Combine(worktreePath, "run-tests.sh"), script.ToString());
            return "sh ./run-tests.sh";
        }

        private static async Task EnsureVesselOnlyAsync(
            TestDatabase testDb,
            string tenantId,
            string vesselId,
            string worktreePath,
            string vesselName = "Test Vessel")
        {
            Armada.Core.Models.TenantMetadata? existing = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existing == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new Armada.Core.Models.TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            Vessel vessel = new Vessel
            {
                Id = vesselId,
                TenantId = tenantId,
                Name = vesselName,
                RepoUrl = "file:///tmp/dod-test.git",
                LocalPath = worktreePath,
                WorkingDirectory = worktreePath,
                DefaultBranch = "main"
            };
            await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        /// <summary>
        /// Create a consumer vessel that declares the producer as a sibling repository, plus its
        /// workflow profile. The consumer's LocalPath points at a throwaway directory; the gate
        /// provisions the real worktree through the git double.
        /// </summary>
        private static async Task EnsureConsumerWithSiblingAndProfileAsync(
            TestDatabase testDb,
            string tenantId,
            string consumerVesselId,
            string consumerName,
            string producerVesselRef,
            string? buildCommand,
            string? testCommand,
            string? containerlessTestCommand)
        {
            Armada.Core.Models.TenantMetadata? existing = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existing == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new Armada.Core.Models.TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            List<SiblingRepo> siblings = new List<SiblingRepo>
            {
                new SiblingRepo { VesselRef = producerVesselRef, RelativePath = "../" + producerVesselRef }
            };

            Vessel consumer = new Vessel
            {
                Id = consumerVesselId,
                TenantId = tenantId,
                Name = consumerName,
                RepoUrl = "file:///tmp/dod-consumer.git",
                LocalPath = CreateTempDir(),
                WorkingDirectory = CreateTempDir(),
                DefaultBranch = "main",
                SiblingRepos = JsonSerializer.Serialize(siblings)
            };
            await testDb.Driver.Vessels.CreateAsync(consumer).ConfigureAwait(false);

            WorkflowProfile profile = new WorkflowProfile
            {
                TenantId = tenantId,
                Name = "Consumer Profile",
                Scope = Armada.Core.Enums.WorkflowProfileScopeEnum.Vessel,
                VesselId = consumerVesselId,
                BuildCommand = buildCommand,
                UnitTestCommand = testCommand,
                ContainerlessUnitTestCommand = containerlessTestCommand,
                IsDefault = true,
                Active = true
            };
            await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
        }

        /// <summary>
        /// A build that records its run, hangs on its first run, and passes on every later run.
        /// </summary>
        private static string HangOnceCommand()
        {
            return "echo run >> gate-runs.log; if [ -e hang.marker ]; then echo ok; exit 0; fi; touch hang.marker; sleep 999";
        }

        /// <summary>
        /// A build that records its run and waits while busy.marker exists, as a command waits on a lock another
        /// command holds, then succeeds.
        /// </summary>
        private static string SharedLockCommand()
        {
            return "echo run >> gate-runs.log; while [ -e busy.marker ]; do sleep 0.05; done; echo ok";
        }

        /// <summary>
        /// A build that records its run and then writes a line every 50 ms without ending, as a slow suite does.
        /// </summary>
        private static string LiveOutputCommand()
        {
            return "echo run >> gate-runs.log; while :; do echo tick; sleep 0.05; done";
        }

        /// <summary>
        /// A build that records its run and hangs on every run.
        /// </summary>
        private static string CountedHangCommand()
        {
            return "echo run >> gate-runs.log; sleep 999";
        }

        private static int CountCommandRuns(string worktreePath)
        {
            string log = Path.Combine(worktreePath, "gate-runs.log");
            if (!File.Exists(log)) return 0;
            int runs = 0;
            foreach (string line in File.ReadAllLines(log))
            {
                if (line.Trim() == "run") runs++;
            }

            return runs;
        }

        /// <summary>
        /// Host load readings served in order, the last repeated, on a clock that only the injected delay moves,
        /// so a wait for load to fall runs without a real wait.
        /// </summary>
        private sealed class FakeHostLoad
        {
            public HostLoadMonitor Monitor { get; }

            public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

            /// <summary>15-minute readings served in order, the last repeated. Empty reads unknown.</summary>
            public double?[] LongReadings { get; set; } = new double?[0];

            private readonly double?[] _Readings;
            private int _Next = 0;
            private int _NextLong = 0;
            private DateTime _Now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            public FakeHostLoad(int logicalCores, params double?[] readings)
            {
                _Readings = readings;
                Monitor = new HostLoadMonitor(ReadLoad, () => logicalCores, () => _Now, Delay, ReadLongLoad);
            }

            private double? ReadLongLoad()
            {
                if (LongReadings.Length == 0) return null;
                double? reading = LongReadings[Math.Min(_NextLong, LongReadings.Length - 1)];
                _NextLong++;
                return reading;
            }

            private double? ReadLoad()
            {
                double? reading = _Readings[Math.Min(_Next, _Readings.Length - 1)];
                _Next++;
                return reading;
            }

            private Task Delay(TimeSpan interval, CancellationToken token)
            {
                Delays.Add(interval);
                _Now = _Now + interval;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// A gate whose command timeout is shorter than the production floor, so the timeout path
        /// can be proven without the test waiting out a real 30 seconds. Without a supplied monitor
        /// host load reads unknown, so a loaded test host never sends a timeout into the load wait.
        /// </summary>
        private sealed class FastTimeoutGate : DefinitionOfDoneGate
        {
            private readonly TimeSpan _Timeout;

            /// <summary>The configured limit of each command the gate ran, in run order.</summary>
            public List<int> ConfiguredSeconds { get; } = new List<int>();

            public FastTimeoutGate(
                DefinitionOfDoneSettings settings,
                DatabaseDriver database,
                LoggingModule logging,
                TimeSpan timeout,
                HostLoadMonitor? hostLoad = null) : base(settings, database, logging, null, null, null, hostLoad ?? new HostLoadMonitor(() => null))
            {
                _Timeout = timeout;
            }

            protected override TimeSpan ResolveCommandTimeout(int configuredSeconds)
            {
                lock (ConfiguredSeconds) ConfiguredSeconds.Add(configuredSeconds);
                return _Timeout;
            }
        }

        #endregion
    }
}
