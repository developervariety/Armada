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
    /// Unit coverage for internal-first deployment execution, approval, and verification flows.
    /// </summary>
    public class DeploymentServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Deployment Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync auto executes deployment and records skipped verification when no checks are configured", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checks = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
                DeploymentEnvironmentService environments = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);
                DeploymentService deployments = new DeploymentService(testDb.Driver, workflowProfiles, environments, checks, logging);

                string tenantId = "ten_deploy_auto";
                string userId = "usr_deploy_auto";
                string workingDirectory = CreateWorkingDirectory("deployment-auto");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = CreateWorkflowProfile(tenantId, userId, vessel.Id, new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "staging",
                            DeployCommand = "echo deploy-staging"
                        }
                    });
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    DeploymentEnvironment environment = await testDb.Driver.Environments.CreateAsync(new DeploymentEnvironment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Name = "staging",
                        Kind = EnvironmentKindEnum.Staging,
                        IsDefault = true,
                        Active = true,
                        RequiresApproval = false
                    }).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Deployment deployment = await deployments.CreateAsync(auth, new DeploymentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        WorkflowProfileId = profile.Id,
                        EnvironmentId = environment.Id,
                        Title = "Deploy Staging",
                        SourceRef = "main",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    AssertStartsWith("dpl_", deployment.Id);
                    AssertEqual(DeploymentStatusEnum.Succeeded, deployment.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.Skipped, deployment.VerificationStatus);
                    AssertFalse(String.IsNullOrWhiteSpace(deployment.DeployCheckRunId), "Expected deploy check run id.");
                    AssertEqual(1, deployment.CheckRunIds.Count);
                    AssertTrue(deployment.CompletedUtc.HasValue, "Expected deployment completion timestamp.");
                    AssertNotNull(deployment.RequestHistorySummary);

                    CheckRun? deployRun = await testDb.Driver.CheckRuns.ReadAsync(deployment.DeployCheckRunId!, null).ConfigureAwait(false);
                    deployRun = NotNull(deployRun);
                    AssertEqual(CheckRunTypeEnum.Deploy, deployRun.Type);
                    AssertEqual(CheckRunStatusEnum.Passed, deployRun.Status);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("ApproveAsync executes pending deployments and DenyAsync marks them denied", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checks = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
                DeploymentEnvironmentService environments = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);
                DeploymentService deployments = new DeploymentService(testDb.Driver, workflowProfiles, environments, checks, logging);

                string tenantId = "ten_deploy_approval";
                string userId = "usr_deploy_approval";
                string workingDirectory = CreateWorkingDirectory("deployment-approval");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = CreateWorkflowProfile(tenantId, userId, vessel.Id, new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "production",
                            DeployCommand = "echo deploy-production"
                        }
                    });
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    DeploymentEnvironment environment = await testDb.Driver.Environments.CreateAsync(new DeploymentEnvironment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Name = "production",
                        Kind = EnvironmentKindEnum.Production,
                        IsDefault = true,
                        Active = true,
                        RequiresApproval = true
                    }).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Deployment pending = await deployments.CreateAsync(auth, new DeploymentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        WorkflowProfileId = profile.Id,
                        EnvironmentId = environment.Id,
                        Title = "Approve Me",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    AssertEqual(DeploymentStatusEnum.PendingApproval, pending.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.NotRun, pending.VerificationStatus);
                    AssertTrue(String.IsNullOrWhiteSpace(pending.DeployCheckRunId), "Pending deployment should not yet have a deploy check run.");

                    Deployment approved = await deployments.ApproveAsync(auth, pending.Id, "Ship it.").ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.Skipped, approved.VerificationStatus);
                    AssertEqual("Ship it.", approved.ApprovalComment);
                    AssertEqual(userId, approved.ApprovedByUserId);
                    AssertFalse(String.IsNullOrWhiteSpace(approved.DeployCheckRunId), "Approved deployment should execute.");

                    Deployment deniedSource = await deployments.CreateAsync(auth, new DeploymentUpsertRequest
                    {
                        VesselId = vessel.Id,
                        WorkflowProfileId = profile.Id,
                        EnvironmentId = environment.Id,
                        Title = "Deny Me",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    Deployment denied = await deployments.DenyAsync(auth, deniedSource.Id, "Not today.").ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Denied, denied.Status);
                    AssertEqual("Not today.", denied.ApprovalComment);
                    AssertEqual(userId, denied.ApprovedByUserId);
                    AssertTrue(denied.CompletedUtc.HasValue, "Denied deployment should complete immediately.");
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
                Name = "Deployment Vessel",
                RepoUrl = "file:///tmp/armada-deployment.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
        }

        private static WorkflowProfile CreateWorkflowProfile(
            string tenantId,
            string userId,
            string vesselId,
            List<WorkflowEnvironmentProfile> environments)
        {
            return new WorkflowProfile
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Deployment Workflow",
                Scope = WorkflowProfileScopeEnum.Vessel,
                VesselId = vesselId,
                IsDefault = true,
                Active = true,
                BuildCommand = "echo build",
                Environments = environments
            };
        }

        private static CheckRun NotNull(CheckRun? run)
        {
            if (run == null) throw new InvalidOperationException("Expected check run to be present.");
            return run;
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
