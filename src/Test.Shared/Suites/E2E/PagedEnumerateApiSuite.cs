namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// The paged replacements for the retired whole-list APIs (DEPRECATION.md, Removed): each returns one page with the
    /// total, and the MCP enumerate tool pages the inbox, CLI permission rules, and CLI permission requests. The retired
    /// originals themselves are covered by E2E.CollectionSurface.
    /// </summary>
    public sealed class PagedEnumerateApiSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.PagedEnumerate";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("collection_routes_page", "Each paged replacement of a whole-list route returns one page with the total", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                HttpClient client = fx.AuthClient;
                await PostAsync(client, "/api/v1/model-endpoints", new { Name = "paged-a", Kind = "Inference", Provider = "OpenAICompatible", BaseUrl = "http://127.0.0.1:9", Model = "m" }).ConfigureAwait(false);
                await PostAsync(client, "/api/v1/model-endpoints", new { Name = "paged-b", Kind = "Inference", Provider = "OpenAICompatible", BaseUrl = "http://127.0.0.1:9", Model = "m" }).ConfigureAwait(false);

                foreach (string route in new[] { "/api/v1/harbors/enumerate", "/api/v1/model-endpoints/enumerate", "/api/v1/planning-sessions/enumerate", "/api/v1/inbox/enumerate", "/api/v1/cli-permissions/rules/enumerate", "/api/v1/cli-permissions/requests/enumerate", "/api/v1/push/devices/enumerate", "/api/v1/tenants/enumerate", "/api/v1/users/enumerate", "/api/v1/credentials/enumerate", "/api/v1/jobs/enumerate", "/api/v1/token-usage/enumerate", "/api/v1/request-history/enumerate", "/api/v1/stage-outcomes/enumerate", "/api/v1/failure-cause-rules/enumerate", "/api/v1/model-probe-runs/enumerate", "/api/v1/battery/packs/enumerate" })
                {
                    EnumerationResult<JsonElement> page = await PageAsync(client, route, 1).ConfigureAwait(false);
                    AssertEqual(1, page.PageSize, route + " echoes the page size");
                    AssertTrue(page.Objects.Count <= 1, route + " returns at most one row per page of one");
                    AssertTrue(page.TotalRecords >= page.Objects.Count, route + " reports the total");
                }

                EnumerationResult<JsonElement> endpoints = await PageAsync(client, "/api/v1/model-endpoints/enumerate", 1).ConfigureAwait(false);
                AssertTrue(endpoints.TotalRecords >= 2, "two endpoints exist: " + endpoints.TotalRecords);
                AssertEqual(1, endpoints.Objects.Count, "page one of size one");
                AssertTrue(endpoints.TotalPages >= 2, "more than one page");
            }));

            cases.Add(Case("child_routes_page", "Paged child lists (refinement sessions, release pull requests, vessel branches) page and answer 404 for a missing parent", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                HttpClient client = fx.AuthClient;
                Objective objective = await JsonHelper.DeserializeAsync<Objective>(await PostAsync(client, "/api/v1/objectives", new { Title = "paged objective" }).ConfigureAwait(false)).ConfigureAwait(false);
                foreach (string route in new[] { "/api/v1/objectives/" + objective.Id + "/refinement-sessions/enumerate", "/api/v1/backlog/" + objective.Id + "/refinement-sessions/enumerate" })
                {
                    EnumerationResult<JsonElement> page = await PageAsync(client, route, 5).ConfigureAwait(false);
                    AssertEqual(5, page.PageSize, route);
                    AssertEqual(0L, page.TotalRecords, route + " has no sessions yet");
                }

                foreach (string route in new[] { "/api/v1/objectives/obj_missing/refinement-sessions/enumerate", "/api/v1/releases/rel_missing/github/pull-requests/enumerate", "/api/v1/vessels/vsl_missing/branches/enumerate" })
                {
                    using HttpResponseMessage response = await client.PostAsync(route, JsonHelper.ToJsonContent(new { PageSize = 5 })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, response.StatusCode, route);
                }
            }));

            cases.Add(Case("mcp_enumerate_inbox_and_cli_rules", "MCP enumerate pages the inbox, CLI permission rules, and CLI permission requests", async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                foreach (string entityType in new[] { "inbox", "cli_permission_rules", "cli_permission_requests" })
                {
                    string reply = await McpTestCalls.CallToolAsync(fx.McpClient, "enumerate", new { entityType = entityType, pageSize = 3 }).ConfigureAwait(false);
                    AssertFalse(reply.Contains("ErrorCode", StringComparison.Ordinal), entityType + ": " + reply);
                    AssertContains("PageSize\\u0022:3", reply, entityType + " echoes the page size: " + reply);
                    AssertContains("TotalRecords", reply, entityType);
                }
            }));

            return new TestSuiteDescriptor(SuiteId, "Paged replacements for whole-list APIs", cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
        {
            HttpResponseMessage response = await client.PostAsync(path, JsonHelper.ToJsonContent(body)).ConfigureAwait(false);
            AssertTrue(response.IsSuccessStatusCode, path + ": " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return response;
        }

        private static async Task<EnumerationResult<JsonElement>> PageAsync(HttpClient client, string route, int pageSize)
        {
            using HttpResponseMessage response = await client.PostAsync(route, JsonHelper.ToJsonContent(new { PageNumber = 1, PageSize = pageSize })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode, route + ": " + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return await JsonHelper.DeserializeAsync<EnumerationResult<JsonElement>>(response).ConfigureAwait(false);
        }

        private TestCaseDescriptor Case(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, caseId, displayName, (CancellationToken ct) => body());
        }

        #endregion
    }
}
