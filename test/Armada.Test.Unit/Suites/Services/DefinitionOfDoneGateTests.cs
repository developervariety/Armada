namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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

            await RunTest("Restore runs before build: sentinel created by restore is present when build checks for it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_rseam", "vsl_rseam",
                        worktreePath, BuildCommandRequiringSentinel(), null).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        RunRestoreBeforeBuild = true,
                        RestoreCommand = CreateSentinelRestoreCommand()
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    Mission mission = CreateWorkerMission("ten_rseam", "vsl_rseam");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass: restore created the sentinel before build checked for it");
                    AssertNull(result.CommandLabel, "No command should be labeled as failing");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Restore runs before unit-test for a build-less profile: sentinel present when test checks for it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    // No build command; only a unit-test command that asserts the restore sentinel exists.
                    // Proves restore runs before the test step (not just before build) for test-only profiles.
                    await EnsureVesselWithProfileAsync(testDb, "ten_rtestonly", "vsl_rtestonly",
                        worktreePath, null, BuildCommandRequiringSentinel()).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        RunRestoreBeforeBuild = true,
                        RestoreCommand = CreateSentinelRestoreCommand()
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    Mission mission = CreateWorkerMission("ten_rtestonly", "vsl_rtestonly");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass: restore created the sentinel before the unit-test step checked for it");
                    AssertNull(result.CommandLabel, "No command should be labeled as failing");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Restore failure short-circuits: gate returns restore failure and build never runs", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_rfail", "vsl_rfail",
                        worktreePath, CreateBuildSentinelCommand(), null).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        RunRestoreBeforeBuild = true,
                        RestoreCommand = FailCommand()
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    Mission mission = CreateWorkerMission("ten_rfail", "vsl_rfail");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertFalse(result.Passed, "Gate must fail when restore fails");
                    AssertEqual("restore", result.CommandLabel, "CommandLabel must identify the restore step");
                    AssertTrue(result.ExitCode != 0, "ExitCode must be non-zero for a failed restore");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "build_ran.txt")),
                        "Build must not run when restore exits non-zero");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Restore disabled by RunRestoreBeforeBuild=false: sentinel absent, gate proceeds to build", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_roff", "vsl_roff",
                        worktreePath, SuccessCommand(), null).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        RunRestoreBeforeBuild = false,
                        RestoreCommand = CreateSentinelRestoreCommand()
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    Mission mission = CreateWorkerMission("ten_roff", "vsl_roff");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass: build succeeded and restore was skipped");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "sentinel.txt")),
                        "Restore must not run when RunRestoreBeforeBuild is false");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Restore disabled by empty RestoreCommand: sentinel absent, gate proceeds to build", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                string worktreePath = CreateTempDir();
                try
                {
                    await EnsureVesselWithProfileAsync(testDb, "ten_rempty", "vsl_rempty",
                        worktreePath, SuccessCommand(), null).ConfigureAwait(false);

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings
                    {
                        Enabled = true,
                        RunRestoreBeforeBuild = true,
                        RestoreCommand = String.Empty
                    };
                    DefinitionOfDoneGate gate = new DefinitionOfDoneGate(settings, testDb.Driver, logging);
                    Mission mission = CreateWorkerMission("ten_rempty", "vsl_rempty");
                    Dock dock = new Dock { WorktreePath = worktreePath };

                    DefinitionOfDoneResult result = await gate.EvaluateAsync(mission, dock).ConfigureAwait(false);

                    AssertTrue(result.Passed, "Gate must pass: build succeeded and empty RestoreCommand means no restore");
                    AssertFalse(File.Exists(Path.Combine(worktreePath, "sentinel.txt")),
                        "Restore must not run when RestoreCommand is empty");
                }
                finally
                {
                    TryDeleteDirectory(worktreePath);
                }
            }).ConfigureAwait(false);

            await RunTest("Settings defaults: RunRestoreBeforeBuild, RestoreCommand, CommandTimeoutSeconds", async () =>
            {
                DefinitionOfDoneSettings defaults = new DefinitionOfDoneSettings();
                AssertTrue(defaults.RunRestoreBeforeBuild, "Default RunRestoreBeforeBuild must be true");
                AssertEqual("dotnet restore", defaults.RestoreCommand, "Default RestoreCommand must be 'dotnet restore'");
                AssertEqual(600, defaults.CommandTimeoutSeconds, "Default CommandTimeoutSeconds must be 600");

                // Clamp [30,3600] still applies.
                AssertEqual(30, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 5 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds below floor must clamp to 30");
                AssertEqual(3600, new DefinitionOfDoneSettings { CommandTimeoutSeconds = 99999 }.CommandTimeoutSeconds,
                    "CommandTimeoutSeconds above ceiling must clamp to 3600");

                // RestoreCommand setter: trim but allow empty (does not force-default empty back to "dotnet restore").
                AssertEqual("dotnet restore", new DefinitionOfDoneSettings { RestoreCommand = "  dotnet restore  " }.RestoreCommand,
                    "RestoreCommand setter must trim leading/trailing whitespace");
                AssertEqual(String.Empty, new DefinitionOfDoneSettings { RestoreCommand = String.Empty }.RestoreCommand,
                    "Empty RestoreCommand must stay empty, not revert to default");
                AssertEqual(String.Empty, new DefinitionOfDoneSettings { RestoreCommand = "   " }.RestoreCommand,
                    "Whitespace-only RestoreCommand must trim to empty, not revert to default");

                await Task.CompletedTask.ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        #region Private-Methods

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
        /// A command that creates sentinel.txt in the working directory to prove it ran.
        /// Used as a RestoreCommand to verify ordering: restore before build.
        /// </summary>
        private static string CreateSentinelRestoreCommand()
        {
            return OperatingSystem.IsWindows()
                ? "echo sentinel > sentinel.txt"
                : "touch sentinel.txt";
        }

        /// <summary>
        /// A build command that exits 0 only when sentinel.txt already exists in the working
        /// directory, proving a prior restore step created it.
        /// </summary>
        private static string BuildCommandRequiringSentinel()
        {
            return OperatingSystem.IsWindows()
                ? "if exist sentinel.txt (exit 0) else (exit 1)"
                : "test -f sentinel.txt";
        }

        /// <summary>
        /// A build command that creates build_ran.txt and exits 0. Used to prove the build step
        /// ran (or did not run) by checking for the file after EvaluateAsync returns.
        /// </summary>
        private static string CreateBuildSentinelCommand()
        {
            return OperatingSystem.IsWindows()
                ? "echo build_ran > build_ran.txt"
                : "touch build_ran.txt";
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
            string? testCommand)
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
