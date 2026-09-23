namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for authentication, API key handling, and CORS headers, ported 1:1
    /// from the retired automated AuthenticationTests suite. Cases run against the shared e2e
    /// server fixture using its authenticated and unauthenticated clients, base URL, and API key.
    /// </summary>
    public sealed class AuthenticationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.Authentication";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Authentication suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("valid_api_key_grants_access_to_protected_endpoint", "ValidApiKey_GrantsAccess_ToProtectedEndpoint", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/fleets").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_status", "ValidApiKey_CanAccessStatus", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_captains", "ValidApiKey_CanAccessCaptains", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_missions", "ValidApiKey_CanAccessMissions", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/missions?pageSize=1").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_voyages", "ValidApiKey_CanAccessVoyages", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/voyages").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_signals", "ValidApiKey_CanAccessSignals", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/signals").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("valid_api_key_can_access_vessels", "ValidApiKey_CanAccessVessels", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/vessels").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            foreach (AuthRefusalRoute route in AuthRefusalRoutes.NoApiKey)
            {
                cases.Add(CaseAsync("no_api_key_" + ToCaseId(route.Scenario) + "_is_refused_with_401", "NoApiKey_" + route.Scenario + "_IsRefusedWith401", TestTags.Negative, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    await AssertRefusedAsync(fx, fx.UnauthClient, route).ConfigureAwait(false);
                }));
            }

            foreach (AuthRefusalRoute route in AuthRefusalRoutes.WrongApiKeyRoutes)
            {
                cases.Add(CaseAsync("wrong_api_key_" + ToCaseId(route.Scenario) + "_is_refused_with_401", "WrongApiKey_" + route.Scenario + "_IsRefusedWith401", TestTags.Negative, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    using (HttpClient wrongKeyClient = CreateClientWithKey(fx.BaseUrl, "X-Api-Key", AuthRefusalRoutes.WrongApiKey))
                        await AssertRefusedAsync(fx, wrongKeyClient, route).ConfigureAwait(false);
                }));
            }

            cases.Add(CaseAsync("health_endpoint_accessible_without_key", "HealthEndpoint_AccessibleWithoutKey", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);
            }));

            cases.Add(CaseAsync("health_endpoint_accessible_with_wrong_key", "HealthEndpoint_AccessibleWithWrongKey", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                HttpClient wrongKeyClient = new HttpClient();
                wrongKeyClient.BaseAddress = new Uri(baseUrl);
                wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "totally-invalid-key");

                HttpResponseMessage response = await wrongKeyClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);

                wrongKeyClient.Dispose();
            }));

            cases.Add(CaseAsync("health_endpoint_accessible_with_valid_key", "HealthEndpoint_AccessibleWithValidKey", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status/health").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                HealthResponse health = await JsonHelper.DeserializeAsync<HealthResponse>(response).ConfigureAwait(false);
                AssertEqual("healthy", health.Status);
            }));

            cases.Add(CaseAsync("api_key_header_case_insensitive_lower_case", "ApiKeyHeader_CaseInsensitive_LowerCase", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;
                string apiKey = fx.ApiKey;

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);

                HttpResponseMessage response = await client.GetAsync("/api/v1/fleets").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                client.Dispose();
            }));

            cases.Add(CaseAsync("cors_headers_allow_origin_is_wildcard", "CorsHeaders_AllowOriginIsWildcard", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status").ConfigureAwait(false);

                string? origin = null;
                if (response.Headers.Contains("Access-Control-Allow-Origin"))
                {
                    origin = response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault();
                }
                else if (response.Content.Headers.Contains("Access-Control-Allow-Origin"))
                {
                    origin = response.Content.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault();
                }

                AssertNotNull(origin);
                AssertEqual("*", origin);
            }));

            cases.Add(CaseAsync("cors_headers_allow_methods_present", "CorsHeaders_AllowMethodsPresent", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status").ConfigureAwait(false);

                bool hasMethods =
                    response.Headers.Contains("Access-Control-Allow-Methods") ||
                    response.Content.Headers.Contains("Access-Control-Allow-Methods");

                Assert(hasMethods, "Expected CORS Allow-Methods header in response");
            }));

            cases.Add(CaseAsync("cors_headers_allow_headers_present", "CorsHeaders_AllowHeadersPresent", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status").ConfigureAwait(false);

                bool hasHeaders =
                    response.Headers.Contains("Access-Control-Allow-Headers") ||
                    response.Content.Headers.Contains("Access-Control-Allow-Headers");

                Assert(hasHeaders, "Expected CORS Allow-Headers header in response");
            }));

            cases.Add(CaseAsync("cors_headers_allow_headers_includes_x_api_key", "CorsHeaders_AllowHeadersIncludesXApiKey", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/status").ConfigureAwait(false);

                string? allowHeaders = null;
                if (response.Headers.Contains("Access-Control-Allow-Headers"))
                {
                    allowHeaders = response.Headers.GetValues("Access-Control-Allow-Headers").FirstOrDefault();
                }
                else if (response.Content.Headers.Contains("Access-Control-Allow-Headers"))
                {
                    allowHeaders = response.Content.Headers.GetValues("Access-Control-Allow-Headers").FirstOrDefault();
                }

                AssertNotNull(allowHeaders);
                Assert(
                    allowHeaders == "*" || allowHeaders!.Contains("X-Api-Key", StringComparison.OrdinalIgnoreCase),
                    "Expected Allow-Headers to be wildcard '*' or include 'X-Api-Key', but got: " + allowHeaders);
            }));

            cases.Add(CaseAsync("authenticated_client_can_perform_full_crud_cycle_fleet", "AuthenticatedClient_CanPerformFullCrudCycle_Fleet", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create
                StringContent createContent = JsonHelper.ToJsonContent(new { Name = "CRUD Fleet" });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/fleets", createContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);

                Fleet createdFleet = await JsonHelper.DeserializeAsync<Fleet>(createResp).ConfigureAwait(false);
                string fleetId = createdFleet.Id;

                // Read
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readResp.StatusCode);
                FleetDetailResponse fleetDetail = await JsonHelper.DeserializeAsync<FleetDetailResponse>(readResp).ConfigureAwait(false);
                AssertEqual("CRUD Fleet", fleetDetail.Fleet?.Name);

                // Update
                StringContent updateContent = JsonHelper.ToJsonContent(new { Name = "Updated CRUD Fleet", Description = "Updated" });
                HttpResponseMessage updateResp = await authClient.PutAsync("/api/v1/fleets/" + fleetId, updateContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updateResp.StatusCode);
                Fleet updatedFleet = await JsonHelper.DeserializeAsync<Fleet>(updateResp).ConfigureAwait(false);
                AssertEqual("Updated CRUD Fleet", updatedFleet.Name);

                // Delete
                HttpResponseMessage deleteResp = await authClient.DeleteAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);

                // Verify deleted
                HttpResponseMessage verifyResp = await authClient.GetAsync("/api/v1/fleets/" + fleetId).ConfigureAwait(false);
                ArmadaErrorResponse verifyError = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(verifyResp).ConfigureAwait(false);
                Assert(
                    verifyError.Error != null || verifyError.Message != null,
                    "Deleted fleet should return error on read");
            }));

            cases.Add(CaseAsync("authenticated_client_can_perform_full_crud_cycle_captain", "AuthenticatedClient_CanPerformFullCrudCycle_Captain", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create
                StringContent createContent = JsonHelper.ToJsonContent(new { Name = "CRUD Captain", Runtime = "ClaudeCode" });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/captains", createContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);

                Captain createdCaptain = await JsonHelper.DeserializeAsync<Captain>(createResp).ConfigureAwait(false);
                string captainId = createdCaptain.Id;

                // Read
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readResp.StatusCode);
                Captain readCaptain = await JsonHelper.DeserializeAsync<Captain>(readResp).ConfigureAwait(false);
                AssertEqual("CRUD Captain", readCaptain.Name);

                // Update
                StringContent updateContent = JsonHelper.ToJsonContent(new { Name = "Updated CRUD Captain" });
                HttpResponseMessage updateResp = await authClient.PutAsync("/api/v1/captains/" + captainId, updateContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updateResp.StatusCode);

                // Delete
                HttpResponseMessage deleteResp = await authClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);

                // Verify deleted
                HttpResponseMessage verifyResp = await authClient.GetAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                ArmadaErrorResponse verifyError = await JsonHelper.DeserializeAsync<ArmadaErrorResponse>(verifyResp).ConfigureAwait(false);
                Assert(
                    verifyError.Error != null || verifyError.Message != null,
                    "Deleted captain should return error on read");
            }));

            cases.Add(CaseAsync("empty_api_key_get_fleets_is_refused_with_401", "EmptyApiKey_GetFleets_IsRefusedWith401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                using (HttpClient emptyKeyClient = CreateClientWithKey(fx.BaseUrl, "X-Api-Key", ""))
                    await AssertRefusedAsync(fx, emptyKeyClient, AuthRefusalRoutes.ListRoute("GetFleets", "/api/v1/fleets")).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("multiple_protected_endpoints_all_accessible_with_valid_key", "MultipleProtectedEndpoints_AllAccessibleWithValidKey", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string[] endpoints = new string[]
                {
                    "/api/v1/fleets",
                    "/api/v1/captains",
                    "/api/v1/missions?pageSize=1",
                    "/api/v1/voyages",
                    "/api/v1/signals",
                    "/api/v1/vessels",
                    "/api/v1/status"
                };

                foreach (string endpoint in endpoints)
                {
                    HttpResponseMessage response = await authClient.GetAsync(endpoint).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);
                }
            }));

            cases.Add(CaseAsync("api_key_is_unique_per_test_instance", "ApiKey_IsUniquePerTestInstance", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string apiKey = fx.ApiKey;

                AssertStartsWith("test-key-", apiKey);
                Assert(apiKey.Length > 20, "API key should be sufficiently long");
            }));

            cases.Add(CaseAsync("no_api_key_delete_fleet_is_refused_with_401_and_fleet_remains", "NoApiKey_DeleteFleet_IsRefusedWith401AndFleetRemains", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                Fleet fleet = await CreateFleetAsync(fx.AuthClient, "DeleteTarget").ConfigureAwait(false);
                await AssertRefusedAsync(fx, fx.UnauthClient, AuthRefusalRoutes.DeleteFleet(fleet.Id)).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("no_api_key_put_fleet_is_refused_with_401_and_name_unchanged", "NoApiKey_PutFleet_IsRefusedWith401AndNameUnchanged", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                Fleet fleet = await CreateFleetAsync(fx.AuthClient, "UpdateTarget").ConfigureAwait(false);
                await AssertRefusedAsync(fx, fx.UnauthClient, AuthRefusalRoutes.PutFleet(fleet.Id, fleet.Name)).ConfigureAwait(false);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Authentication",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task AssertRefusedAsync(E2EServerFixture fx, HttpClient caller, AuthRefusalRoute route)
        {
            string? failure = await AuthRefusalRoutes.DescribeRefusalFailureAsync(caller, fx.AuthClient, route).ConfigureAwait(false);
            AssertTrue(failure == null, failure);
        }

        private static HttpClient CreateClientWithKey(string baseUrl, string headerName, string key)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add(headerName, key);
            return client;
        }

        private static async Task<Fleet> CreateFleetAsync(HttpClient authClient, string name)
        {
            HttpResponseMessage response = await authClient.PostAsync("/api/v1/fleets", JsonHelper.ToJsonContent(new { Name = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, response.StatusCode);
            return await JsonHelper.DeserializeAsync<Fleet>(response).ConfigureAwait(false);
        }

        private static string ToCaseId(string scenario)
        {
            System.Text.StringBuilder id = new System.Text.StringBuilder();
            foreach (char c in scenario)
            {
                if (Char.IsUpper(c) && id.Length > 0) id.Append('_');
                id.Append(Char.ToLowerInvariant(c));
            }
            return id.ToString();
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
    }
}
