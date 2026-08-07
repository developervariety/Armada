namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RequestHistoryCaptureService"/> record building and capture policy.
    /// Cases verify that sensitive headers, query parameters, and JSON body secrets are redacted, that
    /// binary bodies are omitted and oversized text is truncated to the configured byte budget, and that
    /// the API-only and exclusion rules governing which routes are captured are honored.
    /// </summary>
    public sealed class RequestHistoryCaptureServiceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.RequestHistoryCaptureService";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Request History Capture Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("build_record_redacts_headers_query_params_and_body_secrets", "BuildRecord redacts headers, query params, and JSON body secrets", TestTags.Positive, () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                RequestHistoryCaptureService service = new RequestHistoryCaptureService(settings);
                AuthContext auth = AuthContext.Authenticated(
                    tenantId: "ten_capture",
                    userId: "usr_capture",
                    isAdmin: false,
                    isTenantAdmin: true,
                    authMethod: "Bearer",
                    credentialId: "crd_capture",
                    principalDisplay: "captain@armada");

                RequestHistoryRecord record = service.BuildRecord(auth, new RequestHistoryCaptureInput
                {
                    Method = "POST",
                    Route = "/api/v1/missions",
                    QueryString = "?scope=repo&token=secret-token",
                    RequestContentType = "application/json",
                    RequestBodyText = "{\"title\":\"Mission\",\"password\":\"hunter2\",\"gitHubToken\":\"ghp_global\",\"nested\":{\"apiKey\":\"abc123\",\"gitHubTokenOverride\":\"ghp_vessel\"}}",
                    ResponseContentType = "application/json",
                    ResponseBodyText = "{\"ok\":true}",
                    RequestHeaders = new Dictionary<string, string?>
                    {
                        ["Authorization"] = "Bearer raw-token",
                        ["X-Token"] = "raw-session-token",
                        ["X-Correlation-Id"] = "corr-123"
                    },
                    ResponseHeaders = new Dictionary<string, string?>
                    {
                        ["Set-Cookie"] = "armada=secret",
                        ["Content-Type"] = "application/json"
                    }
                });

                AssertEqual("captain@armada", record.Entry.PrincipalDisplay);
                AssertEqual("crd_capture", record.Entry.CredentialId);
                AssertContains("[REDACTED]", record.Detail!.RequestHeadersJson ?? string.Empty);
                AssertFalse((record.Detail.RequestHeadersJson ?? string.Empty).Contains("raw-token"));
                AssertFalse((record.Detail.RequestHeadersJson ?? string.Empty).Contains("raw-session-token"));
                AssertContains("\"token\": \"[REDACTED]\"", record.Detail.QueryParamsJson ?? string.Empty);
                AssertFalse((record.Detail.QueryParamsJson ?? string.Empty).Contains("secret-token"));
                AssertContains("[REDACTED]", record.Detail.RequestBodyText ?? string.Empty);
                AssertFalse((record.Detail.RequestBodyText ?? string.Empty).Contains("hunter2"));
                AssertFalse((record.Detail.RequestBodyText ?? string.Empty).Contains("abc123"));
                AssertFalse((record.Detail.RequestBodyText ?? string.Empty).Contains("ghp_global"));
                AssertFalse((record.Detail.RequestBodyText ?? string.Empty).Contains("ghp_vessel"));
                AssertContains("[REDACTED]", record.Detail.ResponseHeadersJson ?? string.Empty);
            }));

            cases.Add(Case("build_record_omits_binary_bodies_and_truncates_oversized_text", "BuildRecord omits binary bodies and truncates oversized text", TestTags.Positive, () =>
            {
                ArmadaSettings settings = new ArmadaSettings
                {
                    RequestHistoryMaxBodyBytes = 16
                };
                RequestHistoryCaptureService service = new RequestHistoryCaptureService(settings);

                RequestHistoryRecord record = service.BuildRecord(null, new RequestHistoryCaptureInput
                {
                    Method = "POST",
                    Route = "/api/v1/events",
                    RequestContentType = "application/octet-stream",
                    RequestBodyText = "pretend-binary",
                    ResponseContentType = "text/plain",
                    ResponseBodyText = "abcdefghijklmnopqrstuvwxyz"
                });

                AssertEqual("[binary content omitted]", record.Detail!.RequestBodyText);
                AssertFalse(record.Detail.RequestBodyTruncated);
                AssertTrue(record.Detail.ResponseBodyTruncated);
                AssertContains("...[truncated]", record.Detail.ResponseBodyText ?? string.Empty);
            }));

            cases.Add(Case("should_capture_respects_api_only_and_exclusion_rules", "ShouldCapture respects API-only and exclusion rules", TestTags.Positive, () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                RequestHistoryCaptureService service = new RequestHistoryCaptureService(settings);

                AssertFalse(service.ShouldCapture("/dashboard"));
                AssertFalse(service.ShouldCapture("/api/v1/status/health"));
                AssertFalse(service.ShouldCapture("/api/v1/request-history"));
                AssertTrue(service.ShouldCapture("/api/v1/missions"));
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Request History Capture Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
