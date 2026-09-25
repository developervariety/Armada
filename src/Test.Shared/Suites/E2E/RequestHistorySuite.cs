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
    /// End-to-end descriptors for request-history capture, scoping, summaries, and delete flows,
    /// ported 1:1 from the retired automated RequestHistoryTests suite. Cases run sequentially
    /// against the shared e2e server fixture and carry tenant/user/credential/client identifiers and
    /// captured trace markers across cases as suite instance state, mirroring the legacy shared
    /// fields.
    /// </summary>
    public sealed class RequestHistorySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.RequestHistory";

        private string? _TenantAId;
        private string? _TenantAAdminUserId;
        private string? _TenantAAdminCredentialId;
        private HttpClient? _TenantAAdminClient;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Request History Routes suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("setup_create_tenant_a_admin", "Setup_CreateTenantAAdmin", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient adminClient = fx.AuthClient;
                string baseUrl = fx.BaseUrl;

                _TenantAId = await CreateTenantAsync(adminClient, "tenant-a").ConfigureAwait(false);
                UserCredentialResult tenantAAdmin =
                    await CreateUserWithCredentialAsync(adminClient, _TenantAId, "tenant-a-admin", true).ConfigureAwait(false);

                _TenantAAdminUserId = tenantAAdmin.UserId;
                _TenantAAdminCredentialId = tenantAAdmin.CredentialId;
                _TenantAAdminClient = CreateBearerClient(baseUrl, tenantAAdmin.BearerToken);
            }));

            cases.Add(CaseAsync("request_history_captures_backlog_and_refinement_routes", "RequestHistory_CapturesBacklogAndRefinementRoutes", TestTags.Positive, async () =>
            {
                await E2EServerFixture.AcquireAsync(this);

                string backlogTrace = "backlog-route-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string refinementTrace = "refinement-route-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string objectiveId = String.Empty;
                string captainId = String.Empty;
                string sessionId = String.Empty;

                try
                {
                    HttpResponseMessage backlogResponse = await _TenantAAdminClient!.PostAsync(
                        "/api/v1/backlog?trace=" + Uri.EscapeDataString(backlogTrace),
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "RequestHistory Backlog",
                            Description = "Track backlog route capture."
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, backlogResponse.StatusCode);
                    Objective objective = await JsonHelper.DeserializeAsync<Objective>(backlogResponse).ConfigureAwait(false);
                    objectiveId = objective.Id;

                    HttpResponseMessage captainResponse = await _TenantAAdminClient.PostAsync(
                        "/api/v1/captains",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "RequestHistoryRefinement-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            Runtime = "ClaudeCode"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, captainResponse.StatusCode);
                    Captain captain = await JsonHelper.DeserializeAsync<Captain>(captainResponse).ConfigureAwait(false);
                    captainId = captain.Id;

                    HttpResponseMessage refinementResponse = await _TenantAAdminClient.PostAsync(
                        "/api/v1/backlog/" + objectiveId + "/refinement-sessions?trace=" + Uri.EscapeDataString(refinementTrace),
                        JsonHelper.ToJsonContent(new
                        {
                            CaptainId = captainId,
                            Title = "Request history refinement"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, refinementResponse.StatusCode);
                    ObjectiveRefinementSessionDetail detail = await JsonHelper.DeserializeAsync<ObjectiveRefinementSessionDetail>(refinementResponse).ConfigureAwait(false);
                    sessionId = detail.Session.Id;

                    RequestHistoryEntry? backlogEntry = await FindEntryByTraceAsync(
                        _TenantAAdminClient,
                        "/api/v1/backlog",
                        backlogTrace,
                        "POST").ConfigureAwait(false);
                    RequestHistoryEntry? refinementEntry = await FindEntryByTraceAsync(
                        _TenantAAdminClient,
                        "/api/v1/backlog/" + objectiveId + "/refinement-sessions",
                        refinementTrace,
                        "POST").ConfigureAwait(false);

                    AssertNotNull(backlogEntry, "Captured backlog-create request");
                    AssertNotNull(refinementEntry, "Captured backlog-refinement request");

                    RequestHistoryRecord record = await ReadEntryAsync(_TenantAAdminClient, refinementEntry!.Id).ConfigureAwait(false);
                    AssertNotNull(record.Detail, "Backlog-refinement detail");
                    AssertNotNull(record.Detail!.RequestHeadersJson, "Backlog-refinement request headers");
                    AssertNotNull(record.Detail.RequestBodyText, "Backlog-refinement request body");

                    Dictionary<string, string?> headers =
                        JsonHelper.Deserialize<Dictionary<string, string?>>(record.Detail.RequestHeadersJson!);
                    AssertEqual("[REDACTED]", headers["Authorization"], "Authorization header redaction");
                    AssertContains(captainId, record.Detail.RequestBodyText!);
                }
                finally
                {
                    if (!String.IsNullOrWhiteSpace(sessionId))
                        await _TenantAAdminClient!.DeleteAsync("/api/v1/objective-refinement-sessions/" + sessionId).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(objectiveId))
                        await _TenantAAdminClient!.DeleteAsync("/api/v1/backlog/" + objectiveId).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(captainId))
                        await _TenantAAdminClient!.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Request History Routes",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> CreateTenantAsync(HttpClient adminClient, string label)
        {
            HttpResponseMessage response = await adminClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new
                {
                    Name = "rqh-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, response.StatusCode);

            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(response).ConfigureAwait(false);
            return tenant.Id;
        }

        private static async Task<UserCredentialResult> CreateUserWithCredentialAsync(
            HttpClient adminClient,
            string tenantId,
            string label,
            bool isTenantAdmin)
        {
            string email = label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@request-history.armada";
            HttpResponseMessage userResponse = await adminClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = isTenantAdmin
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);

            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await adminClient.PostAsync("/api/v1/credentials",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    UserId = user.Id,
                    Name = label + "-credential"
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, credentialResponse.StatusCode);

            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
            AssertNotNull(credential.BearerToken, "Bearer token");
            return new UserCredentialResult
            {
                UserId = user.Id,
                CredentialId = credential.Id,
                BearerToken = credential.BearerToken
            };
        }

        private static HttpClient CreateBearerClient(string baseUrl, string bearerToken)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

        private static async Task<RequestHistoryEntry?> FindEntryByTraceAsync(
            HttpClient client,
            string route,
            string trace,
            string method,
            int? statusCode = null)
        {
            EnumerationResult<RequestHistoryEntry>? lastResult = null;

            for (int attempt = 0; attempt < 15; attempt++)
            {
                string url = "/api/v1/request-history?route=" + route
                    + "&pageSize=250";

                if (statusCode.HasValue)
                    url += "&statusCode=" + statusCode.Value;

                HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<RequestHistoryEntry> result =
                    await JsonHelper.DeserializeAsync<EnumerationResult<RequestHistoryEntry>>(response).ConfigureAwait(false);
                lastResult = result;

                RequestHistoryEntry? entry = result.Objects.FirstOrDefault(e =>
                    String.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(e.QueryString)
                    && e.QueryString.Contains("trace=" + trace, StringComparison.Ordinal));

                if (entry != null) return entry;

                await Task.Delay(100).ConfigureAwait(false);
            }

            return null;
        }

        private static async Task<RequestHistoryRecord> ReadEntryAsync(HttpClient client, string id)
        {
            HttpResponseMessage response = await client.GetAsync("/api/v1/request-history/" + id).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode);
            return await JsonHelper.DeserializeAsync<RequestHistoryRecord>(response).ConfigureAwait(false);
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

        private sealed class UserCredentialResult
        {
            public string UserId { get; set; } = String.Empty;

            public string CredentialId { get; set; } = String.Empty;

            public string BearerToken { get; set; } = String.Empty;
        }

        #endregion
    }
}
