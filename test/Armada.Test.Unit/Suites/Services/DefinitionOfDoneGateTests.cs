namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit tests for DefinitionOfDoneGate: in-dock build and unit-test gate for Worker missions.
    /// </summary>
    public class DefinitionOfDoneGateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Definition Of Done Gate";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Classify treats a dead container runtime as Infra, not a test failure", () =>
            {
                // Regression: with no container runtime in the dock, every container-backed fixture
                // fails and the runner prints ordinary test-failure text ("Failed: 12"). Matching on
                // that first blamed the code for a missing environment, so a Worker was failed and
                // its downstream personas cancelled over infrastructure.
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();

                string output =
                    "  Starting test execution, please wait...\r\n" +
                    "  Docker.DotNet.DockerApiException: Cannot connect to the Docker daemon at npipe://./pipe/docker_engine. Is the docker daemon running?\r\n" +
                    "Failed!  - Failed:    12, Passed:     0, Skipped:     0, Total:    12";

                AssertEqual(
                    DefinitionOfDoneFailureClassEnum.Infra,
                    classifier.Classify("unit-test", 1, output),
                    "container-runtime unavailability must outrank the test-failure text it produces");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Classify treats a unix docker socket failure as Infra", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output =
                    "Testcontainers: could not connect to /var/run/docker.sock\r\n" +
                    "Failed!  - Failed:     3, Passed:    40";

                AssertEqual(
                    DefinitionOfDoneFailureClassEnum.Infra,
                    classifier.Classify("unit-test", 1, output),
                    "a missing docker socket is infrastructure, not a failing test");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Classify still reports a genuine test failure as TestFail", () =>
            {
                // The container guard must not swallow real failures: without container signals a
                // failing assertion is still the code's fault.
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output =
                    "  Failed MyProject.Tests.WidgetTests.Add_Returns_Sum [12 ms]\r\n" +
                    "  Assert.Equal() Failure: Values differ\r\n" +
                    "Failed!  - Failed:     1, Passed:   204";

                AssertEqual(
                    DefinitionOfDoneFailureClassEnum.TestFail,
                    classifier.Classify("unit-test", 1, output),
                    "a real assertion failure must remain TestFail");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Classify reports a timeout as Timeout and never as a test failure", () =>
            {
                // Acceptance criterion: a ceiling hit is an infra timeout, not a test failure --
                // even when the partial output already contains test-failure text.
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output = "Failed!  - Failed:     2, Passed:     9";

                AssertEqual(
                    DefinitionOfDoneFailureClassEnum.Timeout,
                    classifier.Classify("unit-test", -1, output, timedOut: true),
                    "an exceeded timeout must classify as Timeout regardless of partial output");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Gate passes when both build and unit-test commands succeed", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_pass", "vsl_pass",
                        worktreePath, SuccessCommand(), SuccessCommand()).ConfigureAwait(false);

                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_pass", "vsl_pass");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate should pass when both commands succeed");
                    AssertNull(result.CommandLabel, "No command should be labeled as failing");
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

                    // 30 is the floor of CommandTimeoutSeconds (clamped), so this test waits ~30s
                    // by design to prove the timeout path interrupts a hanging read instead of deadlocking.
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false, CommandTimeoutSeconds = 30 },
                        testDb.Driver,
                        logging);

                    Mission mission = CreateWorkerMission("ten_timeout", "vsl_timeout");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate should fail when a command exceeds the timeout");
                    AssertEqual("build", result.CommandLabel, "Timed-out command should keep its label");
                    AssertEqual(-1, result.ExitCode, "Timeout failure should report exit code -1");
                    AssertNotNull(result.OutputTail, "Timeout failure should carry a message");
                    AssertContains("timed out", result.OutputTail!, "Message should explain the timeout");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
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
                    // RunRestoreBeforeBuild=true. This guards the j1939mitm/armada case where the
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
            string? containerlessTestCommand = null)
        {
            await EnsureVesselOnlyAsync(testDb, tenantId, vesselId, worktreePath).ConfigureAwait(false);

            WorkflowProfile profile = new WorkflowProfile
            {
                TenantId = tenantId,
                Name = "Test Profile",
                Scope = Armada.Core.Enums.WorkflowProfileScopeEnum.Vessel,
                VesselId = vesselId,
                BuildCommand = buildCommand,
                UnitTestCommand = testCommand,
                ContainerlessUnitTestCommand = containerlessTestCommand,
                IsDefault = true,
                Active = true
            };
            await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
        }

        private static async Task EnsureVesselOnlyAsync(
            TestDatabase testDb,
            string tenantId,
            string vesselId,
            string worktreePath)
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
                Name = "Test Vessel",
                RepoUrl = "file:///tmp/dod-test.git",
                LocalPath = worktreePath,
                WorkingDirectory = worktreePath,
                DefaultBranch = "main"
            };
            await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        #endregion
    }
}
