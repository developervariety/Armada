namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    public class RequestHistoryCaptureServiceTests : TestSuite
    {
        public override string Name => "RequestHistoryCaptureService";

        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildRecord redacts headers, query params, and JSON body secrets", () =>
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
                    RequestBodyText = "{\"title\":\"Mission\",\"password\":\"hunter2\",\"nested\":{\"apiKey\":\"abc123\"}}",
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
                AssertContains("[REDACTED]", record.Detail.ResponseHeadersJson ?? string.Empty);
            });

            await RunTest("BuildRecord omits binary bodies and truncates oversized text", () =>
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
            });

            await RunTest("ShouldCapture respects API-only and exclusion rules", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                RequestHistoryCaptureService service = new RequestHistoryCaptureService(settings);

                AssertFalse(service.ShouldCapture("/dashboard"));
                AssertFalse(service.ShouldCapture("/api/v1/status/health"));
                AssertFalse(service.ShouldCapture("/api/v1/request-history"));
                AssertTrue(service.ShouldCapture("/api/v1/missions"));
            });
        }
    }
}
