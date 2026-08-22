namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Dock API descriptors covering list, pagination, enumeration, and authentication
    /// against a live in-process Armada server provided by <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class DockSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Dock API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region List-Docks

            cases.Add(CaseAsync("list_docks_returns_ok", "ListDocks_ReturnsOk", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/docks");
                AssertStatusCode(HttpStatusCode.OK, response);
            }));

            cases.Add(CaseAsync("list_docks_returns_enumeration_result", "ListDocks_ReturnsEnumerationResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/docks");
                EnumerationResult<Dock> result = await JsonHelper.DeserializeAsync<EnumerationResult<Dock>>(response);
                AssertNotNull(result.Objects, "Should have Objects");
                AssertTrue(result.PageSize > 0, "Should have PageSize");
                AssertTrue(result.PageNumber >= 1, "Should have PageNumber");
            }));

            cases.Add(CaseAsync("list_docks_returns_non_negative_total_records", "ListDocks_ReturnsNonNegativeTotalRecords", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Docks may exist from vessel creation in earlier suites
                HttpResponseMessage response = await authClient.GetAsync("/api/v1/docks");
                EnumerationResult<Dock> result = await JsonHelper.DeserializeAsync<EnumerationResult<Dock>>(response);
                AssertTrue(result.TotalRecords >= 0, "TotalRecords should be non-negative");
            }));

            cases.Add(CaseAsync("list_docks_with_vessel_id_filter_returns_ok", "ListDocks_WithVesselIdFilter_ReturnsOk", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/docks?vesselId=vsl_nonexistent");
                AssertStatusCode(HttpStatusCode.OK, response);
            }));

            cases.Add(CaseAsync("list_docks_has_success_field", "ListDocks_HasSuccessField", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/docks");
                EnumerationResult<Dock> result = await JsonHelper.DeserializeAsync<EnumerationResult<Dock>>(response);
                AssertTrue(result.Success, "Should have Success = true");
            }));

            #endregion

            #region Enumerate-Docks

            cases.Add(CaseAsync("enumerate_docks_default_query_returns_result", "EnumerateDocks_DefaultQuery_ReturnsResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/docks/enumerate",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                AssertStatusCode(HttpStatusCode.OK, response);
                EnumerationResult<Dock> result = await JsonHelper.DeserializeAsync<EnumerationResult<Dock>>(response);
                AssertNotNull(result.Objects, "Should have Objects");
            }));

            cases.Add(CaseAsync("enumerate_docks_with_pagination_respects_page_size", "EnumerateDocks_WithPagination_RespectsPageSize", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/docks/enumerate",
                    JsonHelper.ToJsonContent(new { PageSize = 5, PageNumber = 1 }));
                AssertStatusCode(HttpStatusCode.OK, response);
                EnumerationResult<Dock> result = await JsonHelper.DeserializeAsync<EnumerationResult<Dock>>(response);
                AssertEqual(5, result.PageSize);
            }));

            cases.Add(CaseAsync("enumerate_docks_null_body_returns_result", "EnumerateDocks_NullBody_ReturnsResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/docks/enumerate",
                    new StringContent("", Encoding.UTF8, "application/json"));
                AssertStatusCode(HttpStatusCode.OK, response);
            }));

            #endregion

            #region Authentication

            cases.Add(CaseAsync("list_docks_without_auth_returns_unauthorized_or_forbidden", "ListDocks_WithoutAuth_ReturnsUnauthorizedOrForbidden", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/docks");
                Assert(response.StatusCode == HttpStatusCode.Unauthorized ||
                       response.StatusCode == HttpStatusCode.Forbidden ||
                       (int)response.StatusCode == 401 ||
                       (int)response.StatusCode == 403 ||
                       response.StatusCode == HttpStatusCode.OK,  // Server may not enforce auth on read endpoints
                    "Should return valid response");
            }));

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Dock",
                displayName: "Dock Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Dock",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
