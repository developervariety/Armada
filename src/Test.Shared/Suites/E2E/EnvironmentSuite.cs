namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for environment CRUD flows, ported from the retired automated
    /// EnvironmentTests suite. Cases share the singleton e2e server fixture and carry their
    /// created vessel and environment identifiers across cases so a trailing cleanup case can
    /// remove them, mirroring the legacy try/finally teardown.
    /// </summary>
    public sealed class EnvironmentSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.Environment";

        private string _VesselId = String.Empty;
        private string _EnvironmentId = String.Empty;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Environments suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("environments_create_list_read_update_and_delete", "Environments_CreateListReadUpdateAndDelete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage vesselResponse = await authClient.PostAsync("/api/v1/vessels",
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
                _VesselId = vessel.Id;

                HttpResponseMessage createResponse = await authClient.PostAsync("/api/v1/environments",
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = _VesselId,
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
                _EnvironmentId = environment.Id;
                AssertStartsWith("env_", _EnvironmentId);
                AssertEqual(EnvironmentKindEnum.Staging, environment.Kind);
                AssertEqual(_VesselId, environment.VesselId);

                HttpResponseMessage getResponse = await authClient.GetAsync("/api/v1/environments/" + _EnvironmentId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, getResponse.StatusCode);
                DeploymentEnvironment loaded = await JsonHelper.DeserializeAsync<DeploymentEnvironment>(getResponse).ConfigureAwait(false);
                AssertEqual(_EnvironmentId, loaded.Id);

                HttpResponseMessage listResponse = await authClient.GetAsync(
                    "/api/v1/environments?vesselId=" + Uri.EscapeDataString(_VesselId) + "&kind=Staging&pageSize=100").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                EnumerationResult<DeploymentEnvironment> environments = await JsonHelper.DeserializeAsync<EnumerationResult<DeploymentEnvironment>>(listResponse).ConfigureAwait(false);
                AssertTrue(environments.Objects.Exists(current => current.Id == _EnvironmentId), "Expected environment to appear in filtered list.");

                HttpResponseMessage updateResponse = await authClient.PutAsync("/api/v1/environments/" + _EnvironmentId,
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = _VesselId,
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

                HttpResponseMessage deleteResponse = await authClient.DeleteAsync("/api/v1/environments/" + _EnvironmentId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
                _EnvironmentId = String.Empty;

                HttpResponseMessage deletedResponse = await authClient.GetAsync("/api/v1/environments/" + updated.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, deletedResponse.StatusCode);
            }));

            cases.Add(CaseAsync("environments_create_without_auth_returns_401", "Environments_CreateWithoutAuthReturns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/environments",
                    JsonHelper.ToJsonContent(new
                    {
                        VesselId = "ves_missing",
                        Name = "Unauthorized Environment"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("environments_cleanup_resources", "Environments_CleanupResources", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                if (!String.IsNullOrWhiteSpace(_EnvironmentId))
                {
                    try { await authClient.DeleteAsync("/api/v1/environments/" + _EnvironmentId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(_VesselId))
                {
                    try { await authClient.DeleteAsync("/api/v1/vessels/" + _VesselId).ConfigureAwait(false); } catch { }
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Environments",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
