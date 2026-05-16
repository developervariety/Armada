namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for deployment environment service behavior and startup seeding.
    /// </summary>
    public class DeploymentEnvironmentServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Deployment Environment Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync creates environment for accessible vessel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_environment";
                string userId = "usr_environment";
                string workingDirectory = CreateWorkingDirectory("environment-create");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    DeploymentEnvironment environment = await service.CreateAsync(auth, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Name = "Staging",
                        Kind = EnvironmentKindEnum.Staging,
                        BaseUrl = "https://staging.example.test",
                        HealthEndpoint = "/health",
                        IsDefault = true
                    }).ConfigureAwait(false);

                    AssertStartsWith("env_", environment.Id);
                    AssertEqual(vessel.Id, environment.VesselId);
                    AssertEqual(EnvironmentKindEnum.Staging, environment.Kind);
                    AssertEqual(true, environment.IsDefault);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateAsync clears previous default on same vessel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_environment_default";
                string userId = "usr_environment_default";
                string workingDirectory = CreateWorkingDirectory("environment-default");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    DeploymentEnvironment first = await service.CreateAsync(auth, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Name = "Staging",
                        Kind = EnvironmentKindEnum.Staging,
                        IsDefault = true
                    }).ConfigureAwait(false);

                    DeploymentEnvironment second = await service.CreateAsync(auth, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Name = "Production",
                        Kind = EnvironmentKindEnum.Production,
                        IsDefault = true
                    }).ConfigureAwait(false);

                    DeploymentEnvironment? reloadedFirst = await testDb.Driver.Environments.ReadAsync(first.Id, null).ConfigureAwait(false);
                    reloadedFirst = NotNull(reloadedFirst);
                    AssertEqual(false, reloadedFirst.IsDefault);
                    AssertEqual(true, second.IsDefault);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("CreateAsync rejects inaccessible vessel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_environment_missing";
                string userId = "usr_environment_missing";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");

                bool threw = false;
                try
                {
                    await service.CreateAsync(auth, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = "ves_missing",
                        Name = "Nowhere"
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }

                AssertTrue(threw, "Expected inaccessible vessel create to throw.");
            }).ConfigureAwait(false);

            await RunTest("SeedDefaultsAsync seeds workflow profile environments without duplication", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_environment_seed";
                string userId = "usr_environment_seed";
                string workingDirectory = CreateWorkingDirectory("environment-seed");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(CreateWorkflowProfile(tenantId, userId, vessel.Id, "Seed Profile", new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "staging",
                            DeployCommand = "echo deploy staging"
                        },
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "production",
                            DeployCommand = "echo deploy production"
                        }
                    })).ConfigureAwait(false);

                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<DeploymentEnvironment> environments = await testDb.Driver.Environments.EnumerateAllAsync(new DeploymentEnvironmentQuery
                    {
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);

                    AssertEqual(2, environments.Count);
                    AssertTrue(environments.Exists(environment => String.Equals(environment.Name, "staging", StringComparison.OrdinalIgnoreCase)), "Expected staging environment.");
                    AssertTrue(environments.Exists(environment => String.Equals(environment.Name, "production", StringComparison.OrdinalIgnoreCase)), "Expected production environment.");
                    AssertEqual(1, environments.Count(environment => environment.IsDefault));

                    DeploymentEnvironment production = environments.Find(environment =>
                        String.Equals(environment.Name, "production", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Expected seeded production environment.");
                    AssertEqual(true, production.RequiresApproval);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("SeedDefaultsAsync creates fallback development environment when no profile exists", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_environment_seed_fallback";
                string userId = "usr_environment_seed_fallback";
                string workingDirectory = CreateWorkingDirectory("environment-seed-fallback");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<DeploymentEnvironment> environments = await testDb.Driver.Environments.EnumerateAllAsync(new DeploymentEnvironmentQuery
                    {
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);

                    AssertEqual(1, environments.Count);
                    AssertEqual("Development", environments[0].Name);
                    AssertEqual(EnvironmentKindEnum.Development, environments[0].Kind);
                    AssertEqual(true, environments[0].IsDefault);
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
                Name = "Environment Vessel",
                RepoUrl = "file:///tmp/armada-environment.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
        }

        private static WorkflowProfile CreateWorkflowProfile(
            string tenantId,
            string userId,
            string vesselId,
            string name,
            List<WorkflowEnvironmentProfile> environments)
        {
            return new WorkflowProfile
            {
                TenantId = tenantId,
                UserId = userId,
                Name = name,
                Scope = WorkflowProfileScopeEnum.Vessel,
                VesselId = vesselId,
                IsDefault = true,
                Active = true,
                BuildCommand = "echo build",
                Environments = environments
            };
        }

        private static DeploymentEnvironment NotNull(DeploymentEnvironment? environment)
        {
            if (environment == null) throw new InvalidOperationException("Expected environment to be present.");
            return environment;
        }

        private static string CreateWorkingDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
    }
}
