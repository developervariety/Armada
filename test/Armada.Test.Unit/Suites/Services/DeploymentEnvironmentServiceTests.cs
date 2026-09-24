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
            await RunTest("CreateAsync clears previous default on same vessel", async () =>
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

            await RunTest("UpdateAsync refuses to move an environment to a vessel in another tenant", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                DeploymentEnvironmentService service = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);
                string workingDirectory = CreateWorkingDirectory("environment-move");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, "ten_environment_home", "usr_environment_home").ConfigureAwait(false);
                    await EnsureTenantAndUserAsync(testDb, "ten_environment_away", "usr_environment_away").ConfigureAwait(false);
                    Vessel home = CreateVessel("ten_environment_home", "usr_environment_home", workingDirectory);
                    home.Name = "Environment Home Vessel";
                    await testDb.Driver.Vessels.CreateAsync(home).ConfigureAwait(false);
                    Vessel sameTenant = CreateVessel("ten_environment_home", "usr_environment_home", workingDirectory);
                    sameTenant.Name = "Environment Second Vessel";
                    await testDb.Driver.Vessels.CreateAsync(sameTenant).ConfigureAwait(false);
                    Vessel away = CreateVessel("ten_environment_away", "usr_environment_away", workingDirectory);
                    away.Name = "Environment Away Vessel";
                    await testDb.Driver.Vessels.CreateAsync(away).ConfigureAwait(false);

                    AuthContext globalAdmin = AuthContext.Authenticated("ten_environment_home", "usr_environment_home", true, true, "UnitTest");
                    DeploymentEnvironment environment = await service.CreateAsync(globalAdmin, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = home.Id,
                        Name = "Staging",
                        Kind = EnvironmentKindEnum.Staging
                    }).ConfigureAwait(false);

                    await AssertThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(globalAdmin, environment.Id, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = away.Id
                    }), "a move to another tenant's vessel is refused").ConfigureAwait(false);
                    DeploymentEnvironment stored = NotNull(await testDb.Driver.Environments.ReadAsync(environment.Id, new DeploymentEnvironmentQuery()).ConfigureAwait(false));
                    AssertEqual(home.Id, stored.VesselId, "the refused move changes nothing");

                    DeploymentEnvironment moved = await service.UpdateAsync(globalAdmin, environment.Id, new DeploymentEnvironmentUpsertRequest
                    {
                        VesselId = sameTenant.Id
                    }).ConfigureAwait(false);
                    AssertEqual(sameTenant.Id, moved.VesselId, "a move within the tenant still succeeds");
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
