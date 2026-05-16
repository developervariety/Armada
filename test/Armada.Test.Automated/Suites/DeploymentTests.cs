namespace Armada.Test.Automated.Suites
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level coverage for deployment execution, approval, verification, and rollback flows.
    /// </summary>
    public class DeploymentTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;
        private readonly string _BaseUrl;

        /// <inheritdoc />
        public override string Name => "Deployments";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public DeploymentTests(HttpClient authClient, HttpClient unauthClient, string baseUrl)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-deployments-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            string vesselId = String.Empty;
            string workflowProfileId = String.Empty;
            string stagingEnvironmentId = String.Empty;
            string productionEnvironmentId = String.Empty;
            string firstDeploymentId = String.Empty;
            string secondDeploymentId = String.Empty;
            string thirdDeploymentId = String.Empty;

            try
            {
                await RunTest("Deployments_CreateReadListVerifyRollbackApproveAndDeny", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Deployment Vessel",
                            RepoUrl = "file:///tmp/deployment-vessel.git",
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;

                    HttpResponseMessage workflowProfileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Deployment Workflow",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            IsDefault = true,
                            Environments = new[]
                            {
                                new
                                {
                                    EnvironmentName = "staging",
                                    DeployCommand = "echo deploy-staging",
                                    SmokeTestCommand = "echo smoke-staging",
                                    DeploymentVerificationCommand = "echo verify-staging",
                                    RollbackCommand = "echo rollback-staging",
                                    RollbackVerificationCommand = "echo rollback-verify-staging"
                                },
                                new
                                {
                                    EnvironmentName = "production",
                                    DeployCommand = "echo deploy-production",
                                    SmokeTestCommand = "echo smoke-production",
                                    DeploymentVerificationCommand = "echo verify-production",
                                    RollbackCommand = "echo rollback-production",
                                    RollbackVerificationCommand = "echo rollback-verify-production"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, workflowProfileResponse.StatusCode);

                    WorkflowProfile workflowProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(workflowProfileResponse).ConfigureAwait(false);
                    workflowProfileId = workflowProfile.Id;

                    HttpResponseMessage stagingEnvironmentResponse = await _AuthClient.PostAsync("/api/v1/environments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Name = "staging",
                            Kind = EnvironmentKindEnum.Staging,
                            BaseUrl = _BaseUrl,
                            HealthEndpoint = "/api/v1/status/health",
                            RequiresApproval = false,
                            IsDefault = true,
                            Active = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, stagingEnvironmentResponse.StatusCode);

                    DeploymentEnvironment stagingEnvironment = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(stagingEnvironmentResponse).ConfigureAwait(false);
                    stagingEnvironmentId = stagingEnvironment.Id;

                    HttpResponseMessage productionEnvironmentResponse = await _AuthClient.PostAsync("/api/v1/environments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Name = "production",
                            Kind = EnvironmentKindEnum.Production,
                            BaseUrl = _BaseUrl,
                            HealthEndpoint = "/api/v1/status/health",
                            RequiresApproval = true,
                            IsDefault = false,
                            Active = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, productionEnvironmentResponse.StatusCode);

                    DeploymentEnvironment productionEnvironment = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(productionEnvironmentResponse).ConfigureAwait(false);
                    productionEnvironmentId = productionEnvironment.Id;

                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/deployments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            EnvironmentId = stagingEnvironmentId,
                            Title = "Deploy Staging",
                            SourceRef = "refs/heads/main",
                            Summary = "Deploy the current main branch to staging.",
                            Notes = "Automated deployment test",
                            AutoExecute = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createResponse.StatusCode);

                    Deployment created = await JsonHelper.DeserializeAsync<Deployment>(createResponse).ConfigureAwait(false);
                    firstDeploymentId = created.Id;
                    AssertStartsWith("dpl_", firstDeploymentId);
                    AssertEqual(DeploymentStatusEnum.Succeeded, created.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.Passed, created.VerificationStatus);
                    AssertFalse(String.IsNullOrWhiteSpace(created.DeployCheckRunId), "Expected deploy check run id.");
                    AssertFalse(String.IsNullOrWhiteSpace(created.SmokeTestCheckRunId), "Expected smoke test check run id.");
                    AssertFalse(String.IsNullOrWhiteSpace(created.HealthCheckRunId), "Expected health check run id.");
                    AssertFalse(String.IsNullOrWhiteSpace(created.DeploymentVerificationCheckRunId), "Expected deployment verification check run id.");
                    AssertTrue(created.CheckRunIds.Count >= 4, "Expected deployment lifecycle check runs to be linked.");
                    AssertNotNull(created.RequestHistorySummary);

                    HttpResponseMessage getResponse = await _AuthClient.GetAsync("/api/v1/deployments/" + firstDeploymentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getResponse.StatusCode);
                    Deployment loaded = await JsonHelper.DeserializeAsync<Deployment>(getResponse).ConfigureAwait(false);
                    AssertEqual(firstDeploymentId, loaded.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/deployments?vesselId=" + Uri.EscapeDataString(vesselId)
                        + "&status=" + Uri.EscapeDataString(DeploymentStatusEnum.Succeeded.ToString())
                        + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<Deployment> deployments = await JsonHelper.DeserializeAsync<EnumerationResult<Deployment>>(listResponse).ConfigureAwait(false);
                    AssertTrue(deployments.Objects.Exists(current => current.Id == firstDeploymentId), "Expected created deployment in filtered list.");

                    HttpResponseMessage verifyResponse = await _AuthClient.PostAsync("/api/v1/deployments/" + firstDeploymentId + "/verify", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, verifyResponse.StatusCode);
                    Deployment verified = await JsonHelper.DeserializeAsync<Deployment>(verifyResponse).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, verified.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.Passed, verified.VerificationStatus);
                    AssertTrue(verified.CheckRunIds.Count > created.CheckRunIds.Count, "Expected re-verification to append more check runs.");

                    HttpResponseMessage rollbackResponse = await _AuthClient.PostAsync("/api/v1/deployments/" + firstDeploymentId + "/rollback", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, rollbackResponse.StatusCode);
                    Deployment rolledBack = await JsonHelper.DeserializeAsync<Deployment>(rollbackResponse).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.RolledBack, rolledBack.Status);
                    AssertFalse(String.IsNullOrWhiteSpace(rolledBack.RollbackCheckRunId), "Expected rollback check run id.");
                    AssertFalse(String.IsNullOrWhiteSpace(rolledBack.RollbackVerificationCheckRunId), "Expected rollback verification check run id.");

                    HttpResponseMessage pendingCreateResponse = await _AuthClient.PostAsync("/api/v1/deployments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            EnvironmentId = productionEnvironmentId,
                            Title = "Deploy Production",
                            AutoExecute = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, pendingCreateResponse.StatusCode);

                    Deployment pendingApproval = await JsonHelper.DeserializeAsync<Deployment>(pendingCreateResponse).ConfigureAwait(false);
                    secondDeploymentId = pendingApproval.Id;
                    AssertEqual(DeploymentStatusEnum.PendingApproval, pendingApproval.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.NotRun, pendingApproval.VerificationStatus);

                    HttpResponseMessage approveResponse = await _AuthClient.PostAsync(
                        "/api/v1/deployments/" + secondDeploymentId + "/approve",
                        JsonHelper.ToJsonContent(new
                        {
                            Comment = "Approved for production test"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, approveResponse.StatusCode);
                    Deployment approved = await JsonHelper.DeserializeAsync<Deployment>(approveResponse).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Succeeded, approved.Status);
                    AssertEqual(DeploymentVerificationStatusEnum.Passed, approved.VerificationStatus);
                    AssertEqual("Approved for production test", approved.ApprovalComment);

                    HttpResponseMessage deniedCreateResponse = await _AuthClient.PostAsync("/api/v1/deployments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            EnvironmentId = productionEnvironmentId,
                            Title = "Deploy Production Later",
                            AutoExecute = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, deniedCreateResponse.StatusCode);

                    Deployment pendingDenial = await JsonHelper.DeserializeAsync<Deployment>(deniedCreateResponse).ConfigureAwait(false);
                    thirdDeploymentId = pendingDenial.Id;
                    AssertEqual(DeploymentStatusEnum.PendingApproval, pendingDenial.Status);

                    HttpResponseMessage denyResponse = await _AuthClient.PostAsync(
                        "/api/v1/deployments/" + thirdDeploymentId + "/deny",
                        JsonHelper.ToJsonContent(new
                        {
                            Comment = "Denied for this test run"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, denyResponse.StatusCode);
                    Deployment denied = await JsonHelper.DeserializeAsync<Deployment>(denyResponse).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.Denied, denied.Status);
                    AssertEqual("Denied for this test run", denied.ApprovalComment);

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/deployments/" + firstDeploymentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                    firstDeploymentId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/deployments/" + rolledBack.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedResponse.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Deployments_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/deployments",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Deployment"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(firstDeploymentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/deployments/" + firstDeploymentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(secondDeploymentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/deployments/" + secondDeploymentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(thirdDeploymentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/deployments/" + thirdDeploymentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(productionEnvironmentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/environments/" + productionEnvironmentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(stagingEnvironmentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/environments/" + stagingEnvironmentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(workflowProfileId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + workflowProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(vesselId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
                }

                try
                {
                    if (Directory.Exists(workingDirectory))
                        Directory.Delete(workingDirectory, true);
                }
                catch
                {
                }
            }
        }
    }
}
