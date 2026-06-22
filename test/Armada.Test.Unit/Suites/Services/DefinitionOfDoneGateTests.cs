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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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

                    DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings { Enabled = true };
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
                        new DefinitionOfDoneSettings { Enabled = true },
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
