namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="DeploymentService"/>: internal-first deployment execution,
    /// approval, and denial flows over a live SQLite store. Positive cases assert auto-execution
    /// with skipped verification and the approve/deny lifecycle; negative cases cover the audited
    /// null-request and not-found approval paths.
    /// </summary>
    public sealed class DeploymentServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the DeploymentService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_auto_executes_and_skips_verification_when_no_checks", "CreateAsync auto executes deployment and records skipped verification when no checks are configured", TestTags.Positive, async () =>
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
            }));

            cases.Add(CaseAsync("approve_executes_pending_and_deny_marks_denied", "ApproveAsync executes pending deployments and DenyAsync marks them denied", TestTags.Positive, async () =>
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
            }));

            cases.Add(CaseAsync("vessel_hold_refuses_create_and_approve_for_its_vessel_only", "A vessel hold refuses creating and approving a deployment of its vessel, leaves another vessel deployable, and lifts on clear", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DeploymentService deployments = CreateDeploymentService(testDb);

                string tenantId = "ten_deploy_vessel_hold";
                string userId = "usr_deploy_vessel_hold";
                string heldDirectory = CreateWorkingDirectory("deployment-held");
                string otherDirectory = CreateWorkingDirectory("deployment-other");

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    (Vessel held, DeploymentUpsertRequest heldRequest) = await CreateApprovalVesselAsync(testDb, tenantId, userId, heldDirectory).ConfigureAwait(false);
                    (Vessel other, DeploymentUpsertRequest otherRequest) = await CreateApprovalVesselAsync(testDb, tenantId, userId, otherDirectory).ConfigureAwait(false);

                    Deployment pending = await deployments.CreateAsync(auth, heldRequest).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.PendingApproval, pending.Status);

                    // The hold is engaged through a separate instance over the same store, as another host would.
                    DispatchHold engagingHost = new DispatchHold(testDb.Driver.DispatchHolds);
                    await engagingHost.EngageVesselAsync(held.Id, "consumer gap open", "session-hold").ConfigureAwait(false);

                    string createRefusal = await RefusalAsync(() => deployments.CreateAsync(auth, heldRequest)).ConfigureAwait(false);
                    AssertStartsWith(DispatchHoldActiveException.VesselReasonCode, createRefusal);
                    AssertContains(held.Id, createRefusal);
                    AssertContains("consumer gap open", createRefusal);
                    List<Deployment> heldDeployments = await testDb.Driver.Deployments.EnumerateAllAsync(new DeploymentQuery { VesselId = held.Id }).ConfigureAwait(false);
                    AssertEqual(1, heldDeployments.Count, "a refused create writes no deployment");

                    string approveRefusal = await RefusalAsync(() => deployments.ApproveAsync(auth, pending.Id, "Ship it.")).ConfigureAwait(false);
                    AssertStartsWith(DispatchHoldActiveException.VesselReasonCode, approveRefusal);
                    Deployment stillPending = NotNull(await testDb.Driver.Deployments.ReadAsync(pending.Id).ConfigureAwait(false));
                    AssertEqual(DeploymentStatusEnum.PendingApproval, stillPending.Status);
                    AssertTrue(String.IsNullOrWhiteSpace(stillPending.DeployCheckRunId), "a refused approval runs no deploy");

                    Deployment otherPending = await deployments.CreateAsync(auth, otherRequest).ConfigureAwait(false);
                    Deployment otherApproved = await deployments.ApproveAsync(auth, otherPending.Id, "Ship it.").ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, otherApproved.Status, "another vessel deploys while one vessel is held");

                    await engagingHost.ClearVesselAsync(held.Id).ConfigureAwait(false);
                    Deployment approved = await deployments.ApproveAsync(auth, pending.Id, "Ship it.").ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status, "the held vessel deploys once its hold clears");
                }
                finally
                {
                    TryDeleteDirectory(heldDirectory);
                    TryDeleteDirectory(otherDirectory);
                }
            }));

            cases.Add(CaseAsync("pinned_commit_ships_after_branch_moves", "A deployment ships the commit its ref named at create, even after the branch moves before approval", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repo = TestGitRepoHelper.CreateWorkingRepoCopy();
                    string pinned = RunGit(repo, "rev-parse", "HEAD").Trim();
                    PinFixture fixture = await CreatePinFixtureAsync(testDb, "ten_deploy_pin", "usr_deploy_pin", repo, true, new List<CheckRunTypeEnum>()).ConfigureAwait(false);

                    Deployment pending = await fixture.Deployments.CreateAsync(fixture.Auth, new DeploymentUpsertRequest
                    {
                        VesselId = fixture.Vessel.Id,
                        WorkflowProfileId = fixture.Profile.Id,
                        EnvironmentId = fixture.Environment.Id,
                        Title = "Pinned",
                        SourceRef = "main",
                        AutoExecute = true
                    }).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.PendingApproval, pending.Status);
                    AssertEqual(pinned, pending.SourceCommit, "create resolves the ref to its full commit");

                    RunGit(repo, "commit", "--allow-empty", "-m", "moved after create");
                    string moved = RunGit(repo, "rev-parse", "HEAD").Trim();
                    AssertNotEqual(pinned, moved, "the fixture branch moved");

                    Deployment approved = await fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, "Ship the reviewed commit.", pinned.Substring(0, 12), null).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status);
                    AssertEqual(pinned, approved.ApprovedSourceCommit, "the approval names the pinned commit");

                    CheckRun deployRun = NotNull(await testDb.Driver.CheckRuns.ReadAsync(approved.DeployCheckRunId!, null).ConfigureAwait(false));
                    AssertEqual(pinned, deployRun.CommitHash, "the deploy Check records the pinned commit, not the moved tip");
                    AssertContains("pinned=" + pinned, deployRun.Output ?? String.Empty, "the deploy command receives the pinned commit");
                    AssertFalse((deployRun.Output ?? String.Empty).Contains(moved, StringComparison.Ordinal), "the deploy command never sees the moved tip");
                    AssertContains(moved, approved.ValidationSummary ?? String.Empty, "the record names the commit the moved ref names now");

                    // A fresh service over the same store stands in for an admiral restart.
                    Deployment reloaded = NotNull(await testDb.Driver.Deployments.ReadAsync(approved.Id, null).ConfigureAwait(false));
                    AssertEqual(pinned, reloaded.SourceCommit, "the pinned commit survives a restart");
                    AssertEqual(pinned, reloaded.ApprovedSourceCommit, "the approved commit survives a restart");
                    AssertEqual(approved.DeployCheckRunId, reloaded.DeployCheckRunId, "the deploy evidence link survives a restart");
                }
            }));

            cases.Add(CaseAsync("approval_bound_to_pinned_candidate", "Approval refuses a different candidate, an update keeps the pin, and an approved deployment's candidate is frozen", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repo = TestGitRepoHelper.CreateWorkingRepoCopy();
                    string pinned = RunGit(repo, "rev-parse", "HEAD").Trim();
                    PinFixture fixture = await CreatePinFixtureAsync(testDb, "ten_deploy_bind", "usr_deploy_bind", repo, true, new List<CheckRunTypeEnum>()).ConfigureAwait(false);

                    Deployment pending = await fixture.Deployments.CreateAsync(fixture.Auth, new DeploymentUpsertRequest
                    {
                        VesselId = fixture.Vessel.Id,
                        WorkflowProfileId = fixture.Profile.Id,
                        EnvironmentId = fixture.Environment.Id,
                        SourceRef = "main",
                        ArtifactIdentity = "app@sha256:aaaa",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    RunGit(repo, "commit", "--allow-empty", "-m", "moved after create");
                    string moved = RunGit(repo, "rev-parse", "HEAD").Trim();

                    Deployment retitled = await fixture.Deployments.UpdateAsync(fixture.Auth, pending.Id, new DeploymentUpsertRequest
                    {
                        Title = "Retitled"
                    }).ConfigureAwait(false);
                    AssertEqual(pinned, retitled.SourceCommit, "an update that keeps the ref keeps the pinned commit");

                    string commitRefusal = await RefusalAsync(() => fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null, moved, null)).ConfigureAwait(false);
                    AssertContains("deployment_candidate_changed", commitRefusal, "approving the moved tip is refused");
                    string artifactRefusal = await RefusalAsync(() => fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null, pinned, "app@sha256:bbbb")).ConfigureAwait(false);
                    AssertContains("deployment_candidate_changed", artifactRefusal, "approving another artifact is refused");

                    Deployment stillPending = NotNull(await testDb.Driver.Deployments.ReadAsync(pending.Id, null).ConfigureAwait(false));
                    AssertEqual(DeploymentStatusEnum.PendingApproval, stillPending.Status, "a refused approval leaves the deployment pending");
                    AssertTrue(String.IsNullOrWhiteSpace(stillPending.DeployCheckRunId), "a refused approval runs no deploy command");

                    Deployment approved = await fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null, pinned, "app@sha256:aaaa").ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status);
                    AssertEqual("app@sha256:aaaa", approved.ApprovedArtifactIdentity);

                    string refRefusal = await RefusalAsync(() => fixture.Deployments.UpdateAsync(fixture.Auth, pending.Id, new DeploymentUpsertRequest
                    {
                        SourceRef = moved
                    })).ConfigureAwait(false);
                    AssertContains("deployment_candidate_frozen", refRefusal, "an executed deployment cannot be re-pointed");
                    string artifactChange = await RefusalAsync(() => fixture.Deployments.UpdateAsync(fixture.Auth, pending.Id, new DeploymentUpsertRequest
                    {
                        ArtifactIdentity = "app@sha256:bbbb"
                    })).ConfigureAwait(false);
                    AssertContains("deployment_candidate_frozen", artifactChange, "an executed deployment cannot ship another artifact");
                }
            }));

            cases.Add(CaseAsync("unresolvable_ref_refuses_create", "A ref that names no commit in the vessel's repository refuses the create", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repo = TestGitRepoHelper.CreateWorkingRepoCopy();
                    PinFixture fixture = await CreatePinFixtureAsync(testDb, "ten_deploy_unresolved", "usr_deploy_unresolved", repo, true, new List<CheckRunTypeEnum>()).ConfigureAwait(false);

                    string refusal = await RefusalAsync(() => fixture.Deployments.CreateAsync(fixture.Auth, new DeploymentUpsertRequest
                    {
                        VesselId = fixture.Vessel.Id,
                        WorkflowProfileId = fixture.Profile.Id,
                        EnvironmentId = fixture.Environment.Id,
                        SourceRef = "no-such-branch",
                        AutoExecute = true
                    })).ConfigureAwait(false);
                    AssertContains("deployment_source_unresolved", refusal, "an unknown ref is never stored unpinned");
                }
            }));

            cases.Add(CaseAsync("required_check_evidence_gates_approval", "Approval requires the environment's required Check type to have passed at the pinned commit", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repo = TestGitRepoHelper.CreateWorkingRepoCopy();
                    string pinned = RunGit(repo, "rev-parse", "HEAD").Trim();
                    PinFixture fixture = await CreatePinFixtureAsync(testDb, "ten_deploy_evidence", "usr_deploy_evidence", repo, true,
                        new List<CheckRunTypeEnum> { CheckRunTypeEnum.UnitTest }).ConfigureAwait(false);

                    Deployment pending = await fixture.Deployments.CreateAsync(fixture.Auth, new DeploymentUpsertRequest
                    {
                        VesselId = fixture.Vessel.Id,
                        WorkflowProfileId = fixture.Profile.Id,
                        EnvironmentId = fixture.Environment.Id,
                        SourceRef = "main",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    string missing = await RefusalAsync(() => fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null)).ConfigureAwait(false);
                    AssertStartsWith(DeploymentEvidenceEvaluator.MissingReason, missing, "no UnitTest record refuses approval");

                    await CreateRecordAsync(testDb, fixture, CheckRunTypeEnum.Build, pinned, "echo build", CheckRunStatusEnum.Passed, 1).ConfigureAwait(false);
                    string wrongType = await RefusalAsync(() => fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null)).ConfigureAwait(false);
                    AssertStartsWith(DeploymentEvidenceEvaluator.MissingReason, wrongType, "a passing Build does not satisfy UnitTest");

                    await CreateRecordAsync(testDb, fixture, CheckRunTypeEnum.UnitTest, pinned, "echo unit", CheckRunStatusEnum.Failed, 2).ConfigureAwait(false);
                    string failed = await RefusalAsync(() => fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null)).ConfigureAwait(false);
                    AssertStartsWith(DeploymentEvidenceEvaluator.FailedReason, failed, "a failed UnitTest at the commit refuses approval");

                    Deployment refused = NotNull(await testDb.Driver.Deployments.ReadAsync(pending.Id, null).ConfigureAwait(false));
                    AssertEqual(DeploymentStatusEnum.PendingApproval, refused.Status, "a refused approval leaves the deployment pending");
                    AssertTrue(String.IsNullOrWhiteSpace(refused.DeployCheckRunId), "a refused approval runs no deploy command");
                    AssertEqual(failed, refused.ValidationSummary, "the refusal is stored on the record");

                    CheckRun pass = await CreateRecordAsync(testDb, fixture, CheckRunTypeEnum.UnitTest, pinned, "echo unit", CheckRunStatusEnum.Passed, 3).ConfigureAwait(false);
                    Deployment approved = await fixture.Deployments.ApproveAsync(fixture.Auth, pending.Id, null).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status);
                    AssertEqual(pass.Id, String.Join(",", approved.ValidationCheckRunIds), "the deciding record is linked as evidence");
                }
            }));

            cases.Add(CaseAsync("auto_execute_refuses_without_required_evidence", "A deployment that needs no approval does not execute without the required Check evidence", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repo = TestGitRepoHelper.CreateWorkingRepoCopy();
                    PinFixture fixture = await CreatePinFixtureAsync(testDb, "ten_deploy_auto_evidence", "usr_deploy_auto_evidence", repo, false,
                        new List<CheckRunTypeEnum> { CheckRunTypeEnum.UnitTest }).ConfigureAwait(false);

                    Deployment deployment = await fixture.Deployments.CreateAsync(fixture.Auth, new DeploymentUpsertRequest
                    {
                        VesselId = fixture.Vessel.Id,
                        WorkflowProfileId = fixture.Profile.Id,
                        EnvironmentId = fixture.Environment.Id,
                        SourceRef = "main",
                        AutoExecute = true
                    }).ConfigureAwait(false);

                    AssertEqual(DeploymentStatusEnum.Failed, deployment.Status, "execution is refused");
                    AssertStartsWith(DeploymentEvidenceEvaluator.MissingReason, deployment.Summary ?? String.Empty);
                    AssertTrue(String.IsNullOrWhiteSpace(deployment.DeployCheckRunId), "no deploy command runs without evidence");
                }
            }));

            // Audit addition: create with a null request throws (confirmed against source).

            cases.Add(CaseAsync("create_null_request_throws", "CreateAsync NullRequest Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DeploymentService deployments = CreateDeploymentService(testDb);

                AuthContext auth = AuthContext.Authenticated("ten_deploy_null", "usr_deploy_null", false, false, "UnitTest");
                await AssertThrowsAsync<ArgumentNullException>(() => deployments.CreateAsync(auth, null!));
            }));

            // Audit addition: approving an unknown deployment throws not-found (confirmed against source).

            cases.Add(CaseAsync("approve_unknown_id_throws", "ApproveAsync UnknownId Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DeploymentService deployments = CreateDeploymentService(testDb);

                string tenantId = "ten_deploy_approve_missing";
                string userId = "usr_deploy_approve_missing";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                await AssertThrowsAsync<InvalidOperationException>(() => deployments.ApproveAsync(auth, "dpl_missing", "n/a"));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.DeploymentService",
                displayName: "Deployment Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static DeploymentService CreateDeploymentService(TestDatabase testDb)
        {
            LoggingModule logging = CreateLogging();
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checks = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            DeploymentEnvironmentService environments = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);
            return new DeploymentService(testDb.Driver, workflowProfiles, environments, checks, logging);
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

        private static async Task<(Vessel Vessel, DeploymentUpsertRequest Request)> CreateApprovalVesselAsync(TestDatabase testDb, string tenantId, string userId, string workingDirectory)
        {
            Vessel draft = CreateVessel(tenantId, userId, workingDirectory);
            draft.Name = "Deployment Vessel " + Path.GetFileName(workingDirectory);
            Vessel vessel = await testDb.Driver.Vessels.CreateAsync(draft).ConfigureAwait(false);
            WorkflowProfile profile = await testDb.Driver.WorkflowProfiles.CreateAsync(CreateWorkflowProfile(tenantId, userId, vessel.Id, new List<WorkflowEnvironmentProfile>
            {
                new WorkflowEnvironmentProfile
                {
                    EnvironmentName = "production",
                    DeployCommand = "echo deploy-production"
                }
            })).ConfigureAwait(false);
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
            return (vessel, new DeploymentUpsertRequest
            {
                VesselId = vessel.Id,
                WorkflowProfileId = profile.Id,
                EnvironmentId = environment.Id,
                Title = "Held Deploy",
                AutoExecute = true
            });
        }

        private static CheckRun NotNull(CheckRun? run)
        {
            if (run == null) throw new InvalidOperationException("Expected check run to be present.");
            return run;
        }

        private static Deployment NotNull(Deployment? deployment)
        {
            if (deployment == null) throw new InvalidOperationException("Expected deployment to be present.");
            return deployment;
        }

        private static async Task<PinFixture> CreatePinFixtureAsync(
            TestDatabase testDb,
            string tenantId,
            string userId,
            string repositoryPath,
            bool requiresApproval,
            List<CheckRunTypeEnum> requiredCheckTypes)
        {
            LoggingModule logging = CreateLogging();
            WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
            VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
            CheckRunService checks = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
            DeploymentEnvironmentService environments = new DeploymentEnvironmentService(testDb.Driver, workflowProfiles, logging);
            DeploymentService deployments = new DeploymentService(testDb.Driver, workflowProfiles, environments, checks, logging);

            await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
            Vessel vessel = CreateVessel(tenantId, userId, repositoryPath);
            await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            string deployCommand = OperatingSystem.IsWindows()
                ? "echo pinned=%ARMADA_DEPLOYMENT_SOURCE_COMMIT%"
                : "echo pinned=$ARMADA_DEPLOYMENT_SOURCE_COMMIT";
            WorkflowProfile profile = CreateWorkflowProfile(tenantId, userId, vessel.Id, new List<WorkflowEnvironmentProfile>
            {
                new WorkflowEnvironmentProfile
                {
                    EnvironmentName = "production",
                    DeployCommand = deployCommand
                }
            });
            profile.UnitTestCommand = "echo unit";
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
                RequiresApproval = requiresApproval,
                RequiredCheckTypes = requiredCheckTypes
            }).ConfigureAwait(false);

            return new PinFixture
            {
                Deployments = deployments,
                Vessel = vessel,
                Profile = profile,
                Environment = environment,
                Auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest")
            };
        }

        private static async Task<CheckRun> CreateRecordAsync(
            TestDatabase testDb,
            PinFixture fixture,
            CheckRunTypeEnum type,
            string commit,
            string command,
            CheckRunStatusEnum status,
            int order)
        {
            DateTime created = DateTime.UtcNow.AddMinutes(order);
            return await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                TenantId = fixture.Vessel.TenantId,
                UserId = fixture.Vessel.UserId,
                VesselId = fixture.Vessel.Id,
                WorkflowProfileId = fixture.Profile.Id,
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                CommitHash = commit,
                Command = command,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1,
                CreatedUtc = created,
                CompletedUtc = created,
                LastUpdateUtc = created
            }).ConfigureAwait(false);
        }

        private static async Task<string> RefusalAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            throw new AssertionException("Expected the deployment action to be refused.");
        }

        private static string RunGit(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using (Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The local Git test command did not start."))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("The local Git test command failed: " + error);
                return output;
            }
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

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.DeploymentService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Types

        private sealed class PinFixture
        {
            public DeploymentService Deployments { get; set; } = null!;

            public Vessel Vessel { get; set; } = null!;

            public WorkflowProfile Profile { get; set; } = null!;

            public DeploymentEnvironment Environment { get; set; } = null!;

            public AuthContext Auth { get; set; } = null!;
        }

        #endregion
    }
}
