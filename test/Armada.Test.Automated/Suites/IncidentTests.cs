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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, vesselResponse).ConfigureAwait(false);

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, workflowProfileResponse).ConfigureAwait(false);

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, environmentResponse).ConfigureAwait(false);

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, releaseResponse).ConfigureAwait(false);

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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, deploymentResponse).ConfigureAwait(false);

                    Deployment deployment = await JsonHelper.DeserializeAsync<Deployment>(deploymentResponse).ConfigureAwait(false);
                    deploymentId = deployment.Id;
                    AssertEqual(DeploymentStatusEnum.Succeeded, deployment.Status);

                    HttpResponseMessage rollbackResponse = await _AuthClient.PostAsync("/api/v1/deployments/" + deploymentId + "/rollback", null).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, rollbackResponse).ConfigureAwait(false);
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
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createIncidentResponse).ConfigureAwait(false);

                    Incident incident = await JsonHelper.DeserializeAsync<Incident>(createIncidentResponse).ConfigureAwait(false);
                    incidentId = incident.Id;
                    AssertStartsWith("inc_", incidentId);
                    AssertEqual(deploymentId, incident.RollbackDeploymentId);

                    HttpResponseMessage getIncidentResponse = await _AuthClient.GetAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, getIncidentResponse).ConfigureAwait(false);
                    Incident loaded = await JsonHelper.DeserializeAsync<Incident>(getIncidentResponse).ConfigureAwait(false);
                    AssertEqual(environmentId, loaded.EnvironmentId);
                    AssertEqual(deploymentId, loaded.DeploymentId);
                    AssertEqual(releaseId, loaded.ReleaseId);
                    AssertEqual(vesselId, loaded.VesselId);

                    HttpResponseMessage updateIncidentResponse = await _AuthClient.PutAsync("/api/v1/incidents/" + incidentId,
                        JsonHelper.ToJsonContent(new
                        {
                            Status = IncidentStatusEnum.Closed,
                            RootCause = "Release configuration pointed at the retired pool",
                            RecoveryNotes = "Rollback completed successfully",
                            Postmortem = "Root cause confirmed and fixed"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, updateIncidentResponse).ConfigureAwait(false);
                    Incident updated = await JsonHelper.DeserializeAsync<Incident>(updateIncidentResponse).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Closed, updated.Status);
                    AssertEqual(deploymentId, updated.DeploymentId);
                    AssertEqual(deploymentId, updated.RollbackDeploymentId);
                    AssertEqual("Root cause confirmed and fixed", updated.Postmortem);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/incidents?deploymentId=" + Uri.EscapeDataString(deploymentId)
                        + "&search=" + Uri.EscapeDataString("fixed")
                        + "&pageSize=100").ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, listResponse).ConfigureAwait(false);
                    EnumerationResult<Incident> incidents = await JsonHelper.DeserializeAsync<EnumerationResult<Incident>>(listResponse).ConfigureAwait(false);
                    AssertTrue(incidents.Objects.Exists(current => current.Id == incidentId), "Expected incident in deployment-scoped search results.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteResponse).ConfigureAwait(false);
                    incidentId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/incidents/" + incident.Id).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NotFound, deletedResponse).ConfigureAwait(false);
                }).ConfigureAwait(false);

                await RunTest("Incidents_CloseRequiresARootCauseThePersonWrote", async () =>
                {
                    const string openedReason = "DoD gate failed: classification=TestFail";
                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/incidents",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Mission failed: root-cause close",
                            Status = IncidentStatusEnum.Open,
                            RootCause = openedReason
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createResponse).ConfigureAwait(false);
                    Incident created = await JsonHelper.DeserializeAsync<Incident>(createResponse).ConfigureAwait(false);
                    incidentId = created.Id;
                    AssertEqual(openedReason, created.OpenedReason);
                    AssertNull(created.RootCauseWrittenBy, "The opened reason is not a person-written cause.");

                    HttpResponseMessage missing = await _AuthClient.PutAsync("/api/v1/incidents/" + created.Id,
                        JsonHelper.ToJsonContent(new { Status = IncidentStatusEnum.Closed })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.BadRequest, missing).ConfigureAwait(false);
                    AssertContains("incident_root_cause_required", await missing.Content.ReadAsStringAsync().ConfigureAwait(false));

                    HttpResponseMessage blank = await _AuthClient.PutAsync("/api/v1/incidents/" + created.Id,
                        JsonHelper.ToJsonContent(new { Status = IncidentStatusEnum.Closed, RootCause = "   " })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.BadRequest, blank).ConfigureAwait(false);
                    AssertContains("incident_root_cause_required", await blank.Content.ReadAsStringAsync().ConfigureAwait(false));

                    HttpResponseMessage unchanged = await _AuthClient.PutAsync("/api/v1/incidents/" + created.Id,
                        JsonHelper.ToJsonContent(new { Status = IncidentStatusEnum.Closed, RootCause = "  " + openedReason + " " })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.BadRequest, unchanged).ConfigureAwait(false);
                    AssertContains("incident_root_cause_unchanged", await unchanged.Content.ReadAsStringAsync().ConfigureAwait(false));

                    HttpResponseMessage stillOpen = await _AuthClient.GetAsync("/api/v1/incidents/" + created.Id).ConfigureAwait(false);
                    Incident unchangedRecord = await JsonHelper.DeserializeAsync<Incident>(stillOpen).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Open, unchangedRecord.Status, "A refused close changes nothing.");

                    DateTime before = DateTime.UtcNow.AddMinutes(-1);
                    HttpResponseMessage written = await _AuthClient.PutAsync("/api/v1/incidents/" + created.Id,
                        JsonHelper.ToJsonContent(new { Status = IncidentStatusEnum.Closed, RootCause = "The gate host ran out of disk" })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, written).ConfigureAwait(false);
                    Incident closed = await JsonHelper.DeserializeAsync<Incident>(written).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Closed, closed.Status);
                    AssertEqual("The gate host ran out of disk", closed.RootCause);
                    AssertEqual(openedReason, closed.OpenedReason, "The automatic text is kept separately.");
                    AssertFalse(String.IsNullOrWhiteSpace(closed.RootCauseWrittenBy), "The written cause names its author.");
                    AssertTrue(closed.RootCauseWrittenUtc.HasValue && closed.RootCauseWrittenUtc.Value >= before, "The written cause carries its time.");
                    AssertFalse(closed.ClosedAutomatically, "A person closed the incident.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/incidents/" + created.Id).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.NoContent, deleteResponse).ConfigureAwait(false);
                    incidentId = String.Empty;

                    HttpResponseMessage closedAtCreate = await _AuthClient.PostAsync("/api/v1/incidents",
                        JsonHelper.ToJsonContent(new { Title = "Created closed", Status = IncidentStatusEnum.Closed })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.BadRequest, closedAtCreate).ConfigureAwait(false);
                    AssertContains("incident_root_cause_required", await closedAtCreate.Content.ReadAsStringAsync().ConfigureAwait(false));
                }).ConfigureAwait(false);

                await RunTest("Incidents_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/incidents",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Incident"
                        })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Unauthorized, response).ConfigureAwait(false);
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
