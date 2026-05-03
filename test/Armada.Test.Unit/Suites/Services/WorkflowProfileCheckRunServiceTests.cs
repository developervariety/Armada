namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for workflow-profile resolution/validation and structured check execution.
    /// </summary>
    public class WorkflowProfileCheckRunServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Workflow Profile and Check Run Services";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ValidateAsync rejects workflow profile with no commands", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_profile",
                    UserId = "usr_profile",
                    Name = "Empty Workflow",
                    Scope = WorkflowProfileScopeEnum.Global
                }).ConfigureAwait(false);

                AssertFalse(result.IsValid);
                AssertContains("At least one build, test, release, deploy, or verification command is required.", String.Join(" ", result.Errors));
            }).ConfigureAwait(false);

            await RunTest("ValidateAsync rejects cross-tenant vessel scope", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_source", "usr_source").ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_other", "usr_other").ConfigureAwait(false);

                Vessel vessel = CreateVessel("ten_source", "usr_source", Path.Combine(Path.GetTempPath(), "armada-profile-cross-" + Guid.NewGuid().ToString("N")));
                await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_other",
                    UserId = "usr_other",
                    Name = "Cross Tenant Vessel Workflow",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = "dotnet --version"
                }).ConfigureAwait(false);

                AssertFalse(result.IsValid);
                AssertContains("Vessel does not belong to the workflow profile tenant.", String.Join(" ", result.Errors));
            }).ConfigureAwait(false);

            await RunTest("ResolveForVesselAsync prefers vessel over fleet over global", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_profile", "usr_profile").ConfigureAwait(false);

                Fleet fleet = new Fleet
                {
                    TenantId = "ten_profile",
                    UserId = "usr_profile",
                    Name = "Resolution Fleet"
                };
                await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-profile-resolve-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    Vessel vessel = CreateVessel("ten_profile", "usr_profile", workingDirectory);
                    vessel.FleetId = fleet.Id;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile global = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Global Workflow",
                        Scope = WorkflowProfileScopeEnum.Global,
                        BuildCommand = "global-build"
                    };
                    WorkflowProfile fleetProfile = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Fleet Workflow",
                        Scope = WorkflowProfileScopeEnum.Fleet,
                        FleetId = fleet.Id,
                        BuildCommand = "fleet-build"
                    };
                    WorkflowProfile vesselProfile = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Vessel Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "vessel-build"
                    };

                    await testDb.Driver.WorkflowProfiles.CreateAsync(global).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(fleetProfile).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(vesselProfile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_profile", "usr_profile", false, false, "UnitTest");
                    WorkflowProfile? resolved = await service.ResolveForVesselAsync(auth, vessel).ConfigureAwait(false);

                    AssertNotNull(resolved);
                    AssertEqual(vesselProfile.Id, resolved!.Id);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("RunAsync executes workflow profile command and collects artifacts", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_checks", "usr_checks").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-check-run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(workingDirectory, "artifacts"));

                try
                {
                    await File.WriteAllTextAsync(Path.Combine(workingDirectory, "artifacts", "existing.txt"), "artifact").ConfigureAwait(false);

                    Vessel vessel = CreateVessel("ten_checks", "usr_checks", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_checks",
                        UserId = "usr_checks",
                        Name = "Build Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "dotnet --version",
                        ExpectedArtifacts = new List<string> { "artifacts/existing.txt" }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_checks", "usr_checks", false, false, "UnitTest");
                    CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Label = "Build"
                    }).ConfigureAwait(false);

                    AssertEqual(CheckRunStatusEnum.Passed, run.Status);
                    AssertEqual(0, run.ExitCode ?? -1);
                    AssertTrue(run.DurationMs.HasValue && run.DurationMs.Value >= 0);
                    AssertTrue(run.Artifacts.Count == 1, "Expected one collected artifact");
                    AssertEqual("artifacts/existing.txt", run.Artifacts[0].Path);
                    AssertContains("passed", run.Summary ?? String.Empty);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("RetryAsync re-executes prior check run", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_retry", "usr_retry").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-check-retry-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_retry", "usr_retry", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_retry",
                        UserId = "usr_retry",
                        Name = "Retry Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        UnitTestCommand = "dotnet --version"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_retry", "usr_retry", false, false, "UnitTest");
                    CheckRun first = await checkRuns.RunAsync(auth, new CheckRunRequest
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.UnitTest,
                        Label = "Unit Tests"
                    }).ConfigureAwait(false);
                    CheckRun retry = await checkRuns.RetryAsync(auth, first.Id).ConfigureAwait(false);

                    AssertNotEqual(first.Id, retry.Id);
                    AssertEqual(CheckRunStatusEnum.Passed, retry.Status);

                    EnumerationResult<CheckRun> allRuns = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                    {
                        TenantId = "ten_retry",
                        UserId = "usr_retry",
                        VesselId = vessel.Id,
                        PageNumber = 1,
                        PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(2L, allRuns.TotalRecords);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static Vessel CreateVessel(string tenantId, string userId, string workingDirectory)
        {
            return new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Workflow Vessel",
                RepoUrl = "file:///tmp/armada-tests.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
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
    }
}
