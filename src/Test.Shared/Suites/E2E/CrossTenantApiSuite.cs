namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end cross-tenant isolation descriptors, ported 1:1 from the retired automated
    /// CrossTenantApiTests suite. Verifies that entities created by one tenant are invisible and
    /// inaccessible to another via list, read, and delete operations. Cases run sequentially against
    /// the shared e2e server fixture and carry tenant/user/credential/client and per-entity
    /// identifiers across cases as suite instance state, mirroring the legacy suite's shared fields.
    /// </summary>
    public sealed class CrossTenantApiSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.CrossTenantApi";

        // Tenant A state
        private string? _TenantAId;
        private string? _UserAId;
        private string? _CredentialAId;
        private string? _BearerTokenA;
        private HttpClient? _ClientA;

        // Tenant B state

        // Per-entity identifiers carried across isolation cases
        private string _FleetAId = null!;
        private string _VesselAId = null!;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Cross-Tenant Isolation API Tests suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("setup_create_tenant_a", "Setup_CreateTenantA", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient adminClient = fx.AuthClient;
                string baseUrl = fx.BaseUrl;

                TenantUserCredentialResult result = await CreateTenantWithUserAsync(adminClient, "tenantA").ConfigureAwait(false);
                _TenantAId = result.TenantId;
                _UserAId = result.UserId;
                _CredentialAId = result.CredentialId;
                _BearerTokenA = result.BearerToken;
                _ClientA = CreateBearerClient(baseUrl, _BearerTokenA);

                AssertNotNull(_TenantAId, "TenantA ID");
                AssertNotNull(_BearerTokenA, "TenantA bearer token");
            }));

            cases.Add(CaseAsync("fleet_create_in_tenant_a_returns_201", "Fleet_CreateInTenantA_Returns201", TestTags.Positive, async () =>
            {
                await E2EServerFixture.AcquireAsync(this);

                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/fleets",
                    JsonHelper.ToJsonContent(new { Name = "xt-fleet-A-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(response).ConfigureAwait(false);
                AssertNotNull(fleet.Id, "Fleet ID");
                _FleetAId = fleet.Id;
            }));

            cases.Add(CaseAsync("vessel_create_in_tenant_a_returns_201", "Vessel_CreateInTenantA_Returns201", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string name = "xt-vessel-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                HttpResponseMessage response = await _ClientA!.PostAsync("/api/v1/vessels",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = name,
                        FleetId = _FleetAId,
                        RepoUrl = "https://example.invalid/" + name + ".git"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(response).ConfigureAwait(false);
                AssertNotNull(vessel.Id, "Vessel ID");
                _VesselAId = vessel.Id;

                // A local repository URL is a server path only a global administrator may set.
                HttpResponseMessage update = await fx.AuthClient.PutAsync("/api/v1/vessels/" + vessel.Id,
                    JsonHelper.ToJsonContent(new
                    {
                        Name = name,
                        FleetId = _FleetAId,
                        RepoUrl = TestRepoHelper.GetLocalBareRepoUrl()
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, update.StatusCode, "Global administrator sets the local repository URL");
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Cross-Tenant Isolation API Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<TenantUserCredentialResult> CreateTenantWithUserAsync(HttpClient adminClient, string label)
        {
            // Create tenant via admin
            string tenantName = "xt-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            HttpResponseMessage tenantResp = await adminClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new { Name = tenantName })).ConfigureAwait(false);
            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResp).ConfigureAwait(false);

            // Create user in tenant
            string email = label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@xt.armada";
            HttpResponseMessage userResp = await adminClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = true
                })).ConfigureAwait(false);
            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResp).ConfigureAwait(false);

            // Create credential (bearer token) for the user
            HttpResponseMessage credResp = await adminClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Name = label + "-cred"
                })).ConfigureAwait(false);
            Credential cred = await JsonHelper.DeserializeAsync<Credential>(credResp).ConfigureAwait(false);

            return new TenantUserCredentialResult
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                CredentialId = cred.Id,
                BearerToken = cred.BearerToken
            };
        }

        private static HttpClient CreateBearerClient(string baseUrl, string bearerToken)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

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

        #region Nested-Types

        private sealed class TenantUserCredentialResult
        {
            public string TenantId { get; set; } = String.Empty;

            public string UserId { get; set; } = String.Empty;

            public string CredentialId { get; set; } = String.Empty;

            public string BearerToken { get; set; } = String.Empty;
        }

        #endregion
    }
}
