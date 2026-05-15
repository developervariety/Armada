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
    /// REST-level coverage for incident create, update, query, and cleanup flows.
    /// </summary>
    public class IncidentTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;
        private readonly string _BaseUrl;

        /// <inheritdoc />
        public override string Name => "Incidents";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public IncidentTests(HttpClient authClient, HttpClient unauthClient, string baseUrl)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-incidents-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            string vesselId = String.Empty;
            string workflowProfileId = String.Empty;
            string environmentId = String.Empty;
            string releaseId = String.Empty;
            string deploymentId = String.Empty;
            string incidentId = String.Empty;

            try
            {
                await RunTest("Incidents_CreateReadUpdateFilterAndDeletePreserveContext", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Incident Vessel",
                            RepoUrl = "file:///tmp/incident-vessel.git",
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
                            Name = "Incident Workflow",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            IsDefault = true,
                            Environments = new[]
                            {
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

                    HttpResponseMessage environmentResponse = await _AuthClient.PostAsync("/api/v1/environments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Name = "production",
                            Kind = EnvironmentKindEnum.Production,
                            BaseUrl = _BaseUrl,
                            HealthEndpoint = "/api/v1/status/health",
                            RequiresApproval = false,
                            IsDefault = true,
                            Active = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, environmentResponse.StatusCode);

                    DeploymentEnvironment environment = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(environmentResponse).ConfigureAwait(false);
                    environmentId = environment.Id;

                    HttpResponseMessage releaseResponse = await _AuthClient.PostAsync("/api/v1/releases",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            Title = "Incident Release",
                            Version = "1.2.3",
                            TagName = "v1.2.3",
                            Status = ReleaseStatusEnum.Candidate
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, releaseResponse.StatusCode);

                    Release release = await JsonHelper.DeserializeAsync<Release>(releaseResponse).ConfigureAwait(false);
                    releaseId = release.Id;

                    HttpResponseMessage deploymentResponse = await _AuthClient.PostAsync("/api/v1/deployments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            EnvironmentId = environmentId,
                            ReleaseId = releaseId,
                            Title = "Deploy Incident Release",
                            SourceRef = "refs/tags/v1.2.3",
                            AutoExecute = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, deploymentResponse.StatusCode);

                    Deployment deployment = await JsonHelper.DeserializeAsync<Deployment>(deploymentResponse).ConfigureAwait(false);
                    deploymentId = deployment.Id;
                    AssertEqual(DeploymentStatusEnum.Succeeded, deployment.Status);

                    HttpResponseMessage rollbackResponse = await _AuthClient.PostAsync("/api/v1/deployments/" + deploymentId + "/rollback", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, rollbackResponse.StatusCode);
                    Deployment rolledBack = await JsonHelper.DeserializeAsync<Deployment>(rollbackResponse).ConfigureAwait(false);
                    AssertEqual(DeploymentStatusEnum.RolledBack, rolledBack.Status);

                    HttpResponseMessage createIncidentResponse = await _AuthClient.PostAsync("/api/v1/incidents",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Production rollback",
                            Summary = "Traffic spike exposed a release regression.",
                            Status = IncidentStatusEnum.Open,
                            Severity = IncidentSeverityEnum.Critical,
                            EnvironmentId = environmentId,
                            EnvironmentName = environment.Name,
                            DeploymentId = deploymentId,
                            ReleaseId = releaseId,
                            VesselId = vesselId,
                            RollbackDeploymentId = deploymentId,
                            Impact = "Elevated 500 responses",
                            RootCause = "Bad release configuration",
                            RecoveryNotes = "Rollback executed",
                            Postmortem = "Initial incident notes"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createIncidentResponse.StatusCode);

                    Incident incident = await JsonHelper.DeserializeAsync<Incident>(createIncidentResponse).ConfigureAwait(false);
                    incidentId = incident.Id;
                    AssertStartsWith("inc_", incidentId);
                    AssertEqual(deploymentId, incident.RollbackDeploymentId);

                    HttpResponseMessage getIncidentResponse = await _AuthClient.GetAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getIncidentResponse.StatusCode);
                    Incident loaded = await JsonHelper.DeserializeAsync<Incident>(getIncidentResponse).ConfigureAwait(false);
                    AssertEqual(environmentId, loaded.EnvironmentId);
                    AssertEqual(deploymentId, loaded.DeploymentId);
                    AssertEqual(releaseId, loaded.ReleaseId);
                    AssertEqual(vesselId, loaded.VesselId);

                    HttpResponseMessage updateIncidentResponse = await _AuthClient.PutAsync("/api/v1/incidents/" + incidentId,
                        JsonHelper.ToJsonContent(new
                        {
                            Status = IncidentStatusEnum.Closed,
                            RecoveryNotes = "Rollback completed successfully",
                            Postmortem = "Root cause confirmed and fixed"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, updateIncidentResponse.StatusCode);
                    Incident updated = await JsonHelper.DeserializeAsync<Incident>(updateIncidentResponse).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Closed, updated.Status);
                    AssertEqual(deploymentId, updated.DeploymentId);
                    AssertEqual(deploymentId, updated.RollbackDeploymentId);
                    AssertEqual("Root cause confirmed and fixed", updated.Postmortem);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/incidents?deploymentId=" + Uri.EscapeDataString(deploymentId)
                        + "&search=" + Uri.EscapeDataString("fixed")
                        + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<Incident> incidents = await JsonHelper.DeserializeAsync<EnumerationResult<Incident>>(listResponse).ConfigureAwait(false);
                    AssertTrue(incidents.Objects.Exists(current => current.Id == incidentId), "Expected incident in deployment-scoped search results.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                    incidentId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/incidents/" + incident.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedResponse.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Incidents_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/incidents",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Incident"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(incidentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(deploymentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/deployments/" + deploymentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(releaseId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(environmentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/environments/" + environmentId).ConfigureAwait(false); } catch { }
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
