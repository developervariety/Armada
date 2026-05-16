namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level coverage for environment CRUD flows.
    /// </summary>
    public class EnvironmentTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Environments";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public EnvironmentTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string vesselId = String.Empty;
            string environmentId = String.Empty;

            try
            {
                await RunTest("Environments_CreateListReadUpdateAndDelete", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Environment Vessel",
                            RepoUrl = "file:///tmp/environment-vessel.git",
                            LocalPath = "C:/temp/environment-vessel",
                            WorkingDirectory = "C:/temp/environment-vessel",
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;

                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/environments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Name = "Staging",
                            Kind = EnvironmentKindEnum.Staging,
                            Description = "Primary staging environment",
                            ConfigurationSource = "helm/values.staging.yaml",
                            BaseUrl = "https://staging.example.test",
                            HealthEndpoint = "/health",
                            AccessNotes = "VPN required",
                            DeploymentRules = "Deploy after checks pass",
                            RequiresApproval = false,
                            IsDefault = true,
                            Active = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createResponse.StatusCode);

                    DeploymentEnvironment environment = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(createResponse).ConfigureAwait(false);
                    environmentId = environment.Id;
                    AssertStartsWith("env_", environmentId);
                    AssertEqual(EnvironmentKindEnum.Staging, environment.Kind);
                    AssertEqual(vesselId, environment.VesselId);

                    HttpResponseMessage getResponse = await _AuthClient.GetAsync("/api/v1/environments/" + environmentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getResponse.StatusCode);
                    DeploymentEnvironment loaded = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(getResponse).ConfigureAwait(false);
                    AssertEqual(environmentId, loaded.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/environments?vesselId=" + Uri.EscapeDataString(vesselId) + "&kind=Staging&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<DeploymentEnvironment> environments = await JsonHelper.DeserializeAsync<EnumerationResult<DeploymentEnvironment>>(listResponse).ConfigureAwait(false);
                    AssertTrue(environments.Objects.Exists(current => current.Id == environmentId), "Expected environment to appear in filtered list.");

                    HttpResponseMessage updateResponse = await _AuthClient.PutAsync("/api/v1/environments/" + environmentId,
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Name = "Production",
                            Kind = EnvironmentKindEnum.Production,
                            Description = "Primary production environment",
                            ConfigurationSource = "helm/values.production.yaml",
                            BaseUrl = "https://prod.example.test",
                            HealthEndpoint = "/ready",
                            AccessNotes = "Break-glass credentials in vault",
                            DeploymentRules = "Two-person approval required",
                            RequiresApproval = true,
                            IsDefault = true,
                            Active = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);

                    DeploymentEnvironment updated = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(updateResponse).ConfigureAwait(false);
                    AssertEqual(EnvironmentKindEnum.Production, updated.Kind);
                    AssertEqual(true, updated.RequiresApproval);
                    AssertEqual("https://prod.example.test", updated.BaseUrl);

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/environments/" + environmentId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                    environmentId = String.Empty;

                    HttpResponseMessage deletedResponse = await _AuthClient.GetAsync("/api/v1/environments/" + updated.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedResponse.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Environments_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/environments",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = "ves_missing",
                            Name = "Unauthorized Environment"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(environmentId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/environments/" + environmentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(vesselId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
                }
            }
        }
    }
}
