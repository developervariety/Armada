namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level tests for request-history capture, scoping, summaries, and delete flows.
    /// </summary>
    public class RequestHistoryTests : TestSuite
    {
        private sealed class UserCredentialResult
        {
            public string UserId { get; set; } = String.Empty;

            public string CredentialId { get; set; } = String.Empty;

            public string BearerToken { get; set; } = String.Empty;
        }

        #region Public-Members

        /// <summary>
        /// Name of this test suite.
        /// </summary>
        public override string Name => "Request History Routes";

        #endregion

        #region Private-Members

        private readonly HttpClient _AdminClient;
        private readonly HttpClient _UnauthClient;
        private readonly string _BaseUrl;

        private string? _TenantAId;
        private string? _TenantAAdminUserId;
        private string? _TenantAAdminCredentialId;
        private HttpClient? _TenantAAdminClient;

        private string? _TenantAUserId;
        private string? _TenantAUserCredentialId;
        private HttpClient? _TenantAUserClient;

        private string? _TenantBId;
        private string? _TenantBAdminUserId;
        private string? _TenantBAdminCredentialId;
        private HttpClient? _TenantBAdminClient;

        private string? _TenantAdminTrace;
        private string? _TenantUserTrace;
        private string? _OtherTenantTrace;
        private string? _AuthFailureTrace;
        private string? _OtherTenantEntryId;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the suite with shared admin and unauthenticated clients.
        /// </summary>
        public RequestHistoryTests(HttpClient authClient, HttpClient unauthClient, string baseUrl)
        {
            _AdminClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
            _BaseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        #endregion

        #region Private-Methods

        private async Task<string> CreateTenantAsync(string label)
        {
            HttpResponseMessage response = await _AdminClient.PostAsync("/api/v1/tenants",
                JsonHelper.ToJsonContent(new
                {
                    Name = "rqh-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, response.StatusCode);

            TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(response).ConfigureAwait(false);
            return tenant.Id;
        }

        private async Task<UserCredentialResult> CreateUserWithCredentialAsync(
            string tenantId,
            string label,
            bool isTenantAdmin)
        {
            string email = label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@request-history.armada";
            HttpResponseMessage userResponse = await _AdminClient.PostAsync("/api/v1/users",
                JsonHelper.ToJsonContent(new
                {
                    TenantId = tenantId,
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    IsTenantAdmin = isTenantAdmin
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);

            UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);

            HttpResponseMessage credentialResponse = await _AdminClient.PostAsync("/api/v1/credentials",
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

        private HttpClient CreateBearerClient(string bearerToken)
        {
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(_BaseUrl);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

        private async Task InvokeStatusAsync(HttpClient client, string trace)
        {
            HttpResponseMessage response = await client.GetAsync("/api/v1/status?trace=" + Uri.EscapeDataString(trace)).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private async Task InvokeAuthenticateFailureAsync(string trace, string password)
        {
            HttpResponseMessage response = await _UnauthClient.PostAsync(
                "/api/v1/authenticate?trace=" + Uri.EscapeDataString(trace),
                JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = "admin@armada",
                    Password = password
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        private async Task<RequestHistoryEntry?> FindEntryByTraceAsync(
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

        private async Task<RequestHistoryRecord> ReadEntryAsync(HttpClient client, string id)
        {
            HttpResponseMessage response = await client.GetAsync("/api/v1/request-history/" + id).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode);
            return await JsonHelper.DeserializeAsync<RequestHistoryRecord>(response).ConfigureAwait(false);
        }

        #endregion

        #region Protected-Methods

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            #region Setup

            await RunTest("Setup_CreateTenantAAdmin", async () =>
            {
                _TenantAId = await CreateTenantAsync("tenant-a").ConfigureAwait(false);
                UserCredentialResult tenantAAdmin =
                    await CreateUserWithCredentialAsync(_TenantAId, "tenant-a-admin", true).ConfigureAwait(false);

                _TenantAAdminUserId = tenantAAdmin.UserId;
                _TenantAAdminCredentialId = tenantAAdmin.CredentialId;
                _TenantAAdminClient = CreateBearerClient(tenantAAdmin.BearerToken);
            }).ConfigureAwait(false);

            await RunTest("Setup_CreateTenantAUser", async () =>
            {
                AssertNotNull(_TenantAId, "Tenant A ID");
                UserCredentialResult tenantAUser =
                    await CreateUserWithCredentialAsync(_TenantAId!, "tenant-a-user", false).ConfigureAwait(false);

                _TenantAUserId = tenantAUser.UserId;
                _TenantAUserCredentialId = tenantAUser.CredentialId;
                _TenantAUserClient = CreateBearerClient(tenantAUser.BearerToken);
            }).ConfigureAwait(false);

            await RunTest("Setup_CreateTenantBAdmin", async () =>
            {
                _TenantBId = await CreateTenantAsync("tenant-b").ConfigureAwait(false);
                UserCredentialResult tenantBAdmin =
                    await CreateUserWithCredentialAsync(_TenantBId, "tenant-b-admin", true).ConfigureAwait(false);

                _TenantBAdminUserId = tenantBAdmin.UserId;
                _TenantBAdminCredentialId = tenantBAdmin.CredentialId;
                _TenantBAdminClient = CreateBearerClient(tenantBAdmin.BearerToken);
            }).ConfigureAwait(false);

            #endregion

            #region Capture-And-Detail

            await RunTest("RequestHistory_ListWithoutAuth_Returns401", async () =>
            {
                HttpResponseMessage response = await _UnauthClient.GetAsync("/api/v1/request-history").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_CapturesStatusRequest_AndRedactsAuthorizationHeader", async () =>
            {
                _TenantAdminTrace = "tenant-admin-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                await InvokeStatusAsync(_TenantAAdminClient!, _TenantAdminTrace).ConfigureAwait(false);

                RequestHistoryEntry? entry = await FindEntryByTraceAsync(
                    _TenantAAdminClient!,
                    "/api/v1/status",
                    _TenantAdminTrace,
                    "GET").ConfigureAwait(false);

                AssertNotNull(entry, "Captured tenant-admin request");
                AssertEqual(_TenantAId, entry!.TenantId);
                AssertEqual(_TenantAAdminUserId, entry.UserId);
                AssertEqual(_TenantAAdminCredentialId, entry.CredentialId);
                AssertEqual("GET", entry.Method);
                AssertEqual("/api/v1/status", entry.Route);
                AssertEqual(200, entry.StatusCode);

                RequestHistoryRecord record = await ReadEntryAsync(_TenantAAdminClient!, entry.Id).ConfigureAwait(false);
                AssertNotNull(record.Detail, "Request detail");
                AssertNotNull(record.Detail!.RequestHeadersJson, "Request headers json");
                AssertNotNull(record.Detail.QueryParamsJson, "Query params json");

                Dictionary<string, string?> headers =
                    JsonHelper.Deserialize<Dictionary<string, string?>>(record.Detail.RequestHeadersJson!);
                Dictionary<string, string?> query =
                    JsonHelper.Deserialize<Dictionary<string, string?>>(record.Detail.QueryParamsJson!);

                AssertEqual("[REDACTED]", headers["Authorization"], "Authorization header redaction");
                AssertEqual(_TenantAdminTrace, query["trace"], "Trace query param");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_CapturesAuthenticateFailure_AndRedactsBodySecrets", async () =>
            {
                string password = "super-secret-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _AuthFailureTrace = "auth-failure-" + Guid.NewGuid().ToString("N").Substring(0, 10);

                await InvokeAuthenticateFailureAsync(_AuthFailureTrace, password).ConfigureAwait(false);

                RequestHistoryEntry? entry = await FindEntryByTraceAsync(
                    _AdminClient,
                    "/api/v1/authenticate",
                    _AuthFailureTrace,
                    "POST",
                    401).ConfigureAwait(false);

                AssertNotNull(entry, "Captured authenticate failure");
                RequestHistoryRecord record = await ReadEntryAsync(_AdminClient, entry!.Id).ConfigureAwait(false);
                AssertNotNull(record.Detail, "Authenticate detail");
                AssertNotNull(record.Detail!.RequestBodyText, "Authenticate request body");
                AssertContains("[REDACTED]", record.Detail.RequestBodyText!);
                AssertFalse(record.Detail.RequestBodyText!.Contains(password, StringComparison.Ordinal), "Secret should be redacted");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_Summary_ReturnsBucketedCounts", async () =>
            {
                string fromUtc = DateTime.UtcNow.AddMinutes(-10).ToString("o");
                string toUtc = DateTime.UtcNow.AddMinutes(10).ToString("o");

                HttpResponseMessage response = await _TenantAAdminClient!.GetAsync(
                    "/api/v1/request-history/summary?route=/api/v1/status&fromUtc="
                    + Uri.EscapeDataString(fromUtc)
                    + "&toUtc=" + Uri.EscapeDataString(toUtc)
                    + "&bucketMinutes=5").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                RequestHistorySummaryResult summary =
                    await JsonHelper.DeserializeAsync<RequestHistorySummaryResult>(response).ConfigureAwait(false);

                AssertTrue(summary.TotalCount >= 1, "Summary total count");
                AssertEqual(5, summary.BucketMinutes);
                AssertTrue(summary.Buckets.Count >= 1, "Summary buckets");
                AssertTrue(summary.Buckets.Any(b => b.TotalCount > 0), "At least one populated bucket");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_CapturesReleaseAndHistoryRoutes", async () =>
            {
                string releaseTrace = "release-route-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string historyTrace = "history-route-" + Guid.NewGuid().ToString("N").Substring(0, 10);

                HttpResponseMessage releaseResponse = await _TenantAAdminClient!.GetAsync(
                    "/api/v1/releases?trace=" + Uri.EscapeDataString(releaseTrace)).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, releaseResponse.StatusCode);

                HttpResponseMessage historyResponse = await _TenantAAdminClient.GetAsync(
                    "/api/v1/history?trace=" + Uri.EscapeDataString(historyTrace)).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, historyResponse.StatusCode);

                RequestHistoryEntry? releaseEntry = await FindEntryByTraceAsync(
                    _TenantAAdminClient,
                    "/api/v1/releases",
                    releaseTrace,
                    "GET").ConfigureAwait(false);
                RequestHistoryEntry? historyEntry = await FindEntryByTraceAsync(
                    _TenantAAdminClient,
                    "/api/v1/history",
                    historyTrace,
                    "GET").ConfigureAwait(false);

                AssertNotNull(releaseEntry, "Captured releases route entry");
                AssertNotNull(historyEntry, "Captured history route entry");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_RedactsGitHubTokenOverrideInVesselPayload", async () =>
            {
                string fleetName = "rqh-github-fleet-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage fleetResponse = await _TenantAAdminClient!.PostAsync(
                    "/api/v1/fleets",
                    JsonHelper.ToJsonContent(new
                    {
                        Name = fleetName
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, fleetResponse.StatusCode);
                Fleet fleet = await JsonHelper.DeserializeAsync<Fleet>(fleetResponse).ConfigureAwait(false);

                string trace = "vessel-github-token-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string token = "ghp_request_history_" + Guid.NewGuid().ToString("N").Substring(0, 10);
                HttpResponseMessage vesselResponse = await _TenantAAdminClient.PostAsync(
                    "/api/v1/vessels?trace=" + Uri.EscapeDataString(trace),
                    JsonHelper.ToJsonContent(new
                    {
                        Name = "RequestHistoryGitHubOverride",
                        FleetId = fleet.Id,
                        RepoUrl = "https://github.com/test/request-history-github-override",
                        GitHubTokenOverride = token
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                RequestHistoryEntry? entry = await FindEntryByTraceAsync(
                    _TenantAAdminClient,
                    "/api/v1/vessels",
                    trace,
                    "POST").ConfigureAwait(false);
                AssertNotNull(entry, "Captured vessel-create request");

                RequestHistoryRecord record = await ReadEntryAsync(_TenantAAdminClient, entry!.Id).ConfigureAwait(false);
                AssertNotNull(record.Detail, "Vessel-create detail");
                AssertNotNull(record.Detail!.RequestBodyText, "Vessel-create request body");
                AssertContains("[REDACTED]", record.Detail.RequestBodyText!);
                AssertFalse(record.Detail.RequestBodyText!.Contains(token, StringComparison.Ordinal), "GitHub token override should be redacted");
            }).ConfigureAwait(false);

            #endregion

            #region Scope

            await RunTest("RequestHistory_Scope_RegularUser_OnlySeesOwnEntries", async () =>
            {
                _TenantUserTrace = "tenant-user-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                _OtherTenantTrace = "other-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 10);

                await InvokeStatusAsync(_TenantAUserClient!, _TenantUserTrace).ConfigureAwait(false);
                await InvokeStatusAsync(_TenantBAdminClient!, _OtherTenantTrace).ConfigureAwait(false);

                RequestHistoryEntry? otherTenantEntry = await FindEntryByTraceAsync(
                    _TenantBAdminClient!,
                    "/api/v1/status",
                    _OtherTenantTrace,
                    "GET").ConfigureAwait(false);
                AssertNotNull(otherTenantEntry, "Other-tenant entry");
                _OtherTenantEntryId = otherTenantEntry!.Id;

                HttpResponseMessage response = await _TenantAUserClient!.GetAsync(
                    "/api/v1/request-history?route=/api/v1/status&pageSize=250").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<RequestHistoryEntry> result =
                    await JsonHelper.DeserializeAsync<EnumerationResult<RequestHistoryEntry>>(response).ConfigureAwait(false);

                AssertTrue(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_TenantUserTrace!, StringComparison.Ordinal)),
                    "Regular user should see own request");
                AssertFalse(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_TenantAdminTrace!, StringComparison.Ordinal)),
                    "Regular user should not see tenant-admin sibling request");
                AssertFalse(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_OtherTenantTrace!, StringComparison.Ordinal)),
                    "Regular user should not see other-tenant request");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_Scope_TenantAdmin_SeesTenantNotOtherTenant", async () =>
            {
                HttpResponseMessage response = await _TenantAAdminClient!.GetAsync(
                    "/api/v1/request-history?route=/api/v1/status&pageSize=250").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<RequestHistoryEntry> result =
                    await JsonHelper.DeserializeAsync<EnumerationResult<RequestHistoryEntry>>(response).ConfigureAwait(false);

                AssertTrue(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_TenantAdminTrace!, StringComparison.Ordinal)),
                    "Tenant admin should see own request");
                AssertTrue(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_TenantUserTrace!, StringComparison.Ordinal)),
                    "Tenant admin should see same-tenant user request");
                AssertFalse(result.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(_OtherTenantTrace!, StringComparison.Ordinal)),
                    "Tenant admin should not see other-tenant request");
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_Scope_TenantAdmin_CannotReadOtherTenantEntry", async () =>
            {
                HttpResponseMessage response = await _TenantAAdminClient!.GetAsync("/api/v1/request-history/" + _OtherTenantEntryId).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_Scope_GlobalAdmin_CanFilterByTenant", async () =>
            {
                HttpResponseMessage response = await _AdminClient.GetAsync(
                    "/api/v1/request-history?route=/api/v1/status&tenantId=" + Uri.EscapeDataString(_TenantAId!) + "&pageSize=250").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<RequestHistoryEntry> result =
                    await JsonHelper.DeserializeAsync<EnumerationResult<RequestHistoryEntry>>(response).ConfigureAwait(false);

                AssertTrue(result.Objects.Count >= 2, "Expected tenant-A request history");
                AssertTrue(result.Objects.All(e => e.TenantId == _TenantAId), "Admin tenant filter should constrain rows");
            }).ConfigureAwait(false);

            #endregion

            #region Deletes

            await RunTest("RequestHistory_DeleteSingle_RemovesEntry", async () =>
            {
                string trace = "delete-single-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                await InvokeStatusAsync(_TenantAAdminClient!, trace).ConfigureAwait(false);

                RequestHistoryEntry? entry = await FindEntryByTraceAsync(
                    _TenantAAdminClient!,
                    "/api/v1/status",
                    trace,
                    "GET").ConfigureAwait(false);
                AssertNotNull(entry, "Delete-single entry");

                HttpResponseMessage deleteResponse = await _TenantAAdminClient!.DeleteAsync("/api/v1/request-history/" + entry!.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

                HttpResponseMessage readResponse = await _TenantAAdminClient.GetAsync("/api/v1/request-history/" + entry.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, readResponse.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_DeleteMultiple_RemovesEntries_AndSkipsUnknown", async () =>
            {
                string traceOne = "delete-multi-a-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string traceTwo = "delete-multi-b-" + Guid.NewGuid().ToString("N").Substring(0, 10);

                await InvokeStatusAsync(_TenantAAdminClient!, traceOne).ConfigureAwait(false);
                await InvokeStatusAsync(_TenantAAdminClient!, traceTwo).ConfigureAwait(false);

                RequestHistoryEntry? entryOne = await FindEntryByTraceAsync(_TenantAAdminClient!, "/api/v1/status", traceOne, "GET").ConfigureAwait(false);
                RequestHistoryEntry? entryTwo = await FindEntryByTraceAsync(_TenantAAdminClient!, "/api/v1/status", traceTwo, "GET").ConfigureAwait(false);
                AssertNotNull(entryOne, "Delete-multiple entry one");
                AssertNotNull(entryTwo, "Delete-multiple entry two");

                HttpResponseMessage response = await _TenantAAdminClient!.PostAsync(
                    "/api/v1/request-history/delete/multiple",
                    JsonHelper.ToJsonContent(new
                    {
                        Ids = new[] { entryOne!.Id, entryTwo!.Id, "req_missing" }
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                DeleteMultipleResult result = await JsonHelper.DeserializeAsync<DeleteMultipleResult>(response).ConfigureAwait(false);
                AssertEqual(2, result.Deleted);
                AssertEqual(1, result.Skipped.Count);

                HttpResponseMessage readOne = await _TenantAAdminClient.GetAsync("/api/v1/request-history/" + entryOne.Id).ConfigureAwait(false);
                HttpResponseMessage readTwo = await _TenantAAdminClient.GetAsync("/api/v1/request-history/" + entryTwo.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, readOne.StatusCode);
                AssertEqual(HttpStatusCode.NotFound, readTwo.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("RequestHistory_DeleteByFilter_RemovesScopedEntries", async () =>
            {
                string traceOne = "delete-filter-a-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string traceTwo = "delete-filter-b-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                string fromUtc = DateTime.UtcNow.AddMinutes(-1).ToString("o");

                await InvokeStatusAsync(_TenantAUserClient!, traceOne).ConfigureAwait(false);
                await InvokeStatusAsync(_TenantAUserClient!, traceTwo).ConfigureAwait(false);
                string toUtc = DateTime.UtcNow.AddMinutes(1).ToString("o");

                HttpResponseMessage response = await _TenantAAdminClient!.PostAsync(
                    "/api/v1/request-history/delete/by-filter",
                    JsonHelper.ToJsonContent(new
                    {
                        UserId = _TenantAUserId,
                        Route = "/api/v1/status",
                        FromUtc = fromUtc,
                        ToUtc = toUtc
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                DeleteMultipleResult result = await JsonHelper.DeserializeAsync<DeleteMultipleResult>(response).ConfigureAwait(false);
                AssertTrue(result.Deleted >= 2, "Expected at least the two scoped user requests to be deleted");

                HttpResponseMessage listResponse = await _TenantAUserClient!.GetAsync(
                    "/api/v1/request-history?route=/api/v1/status&pageSize=250").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);

                EnumerationResult<RequestHistoryEntry> remaining =
                    await JsonHelper.DeserializeAsync<EnumerationResult<RequestHistoryEntry>>(listResponse).ConfigureAwait(false);

                AssertFalse(remaining.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(traceOne, StringComparison.Ordinal)),
                    "Filtered delete should remove first scoped entry");
                AssertFalse(remaining.Objects.Any(e => e.QueryString != null && e.QueryString.Contains(traceTwo, StringComparison.Ordinal)),
                    "Filtered delete should remove second scoped entry");
            }).ConfigureAwait(false);

            #endregion

            #region Cleanup

            await RunTest("Cleanup_DeleteRequestHistoryTenants", async () =>
            {
                _TenantAAdminClient?.Dispose();
                _TenantAUserClient?.Dispose();
                _TenantBAdminClient?.Dispose();

                if (_TenantAUserCredentialId != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _TenantAUserCredentialId).ConfigureAwait(false);
                if (_TenantAAdminCredentialId != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _TenantAAdminCredentialId).ConfigureAwait(false);
                if (_TenantBAdminCredentialId != null)
                    await _AdminClient.DeleteAsync("/api/v1/credentials/" + _TenantBAdminCredentialId).ConfigureAwait(false);

                if (_TenantAUserId != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _TenantAUserId).ConfigureAwait(false);
                if (_TenantAAdminUserId != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _TenantAAdminUserId).ConfigureAwait(false);
                if (_TenantBAdminUserId != null)
                    await _AdminClient.DeleteAsync("/api/v1/users/" + _TenantBAdminUserId).ConfigureAwait(false);

                if (_TenantAId != null)
                    await _AdminClient.DeleteAsync("/api/v1/tenants/" + _TenantAId).ConfigureAwait(false);
                if (_TenantBId != null)
                    await _AdminClient.DeleteAsync("/api/v1/tenants/" + _TenantBId).ConfigureAwait(false);
            }).ConfigureAwait(false);

            #endregion
        }

        #endregion
    }
}
