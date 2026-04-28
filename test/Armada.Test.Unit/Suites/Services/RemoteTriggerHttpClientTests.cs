namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    public class RemoteTriggerHttpClientTests : TestSuite
    {
        public override string Name => "RemoteTrigger HttpClient";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FireAsync_2xxResponse_ReturnsSuccess_WithSessionUrl", async () =>
            {
                string responseJson = "{\"type\":\"routine_fire\",\"claude_code_session_id\":\"sess_123\",\"claude_code_session_url\":\"https://claude.ai/code/sessions/sess_123\"}";
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, responseJson);
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireRequest req = MakeRequest();
                FireResult result = await client.FireAsync(req);

                AssertEqual(FireOutcome.Success, result.Outcome);
                AssertEqual(200, result.StatusCode);
                AssertNotNull(result.SessionUrl);
                AssertContains("sess_123", result.SessionUrl!);
            });

            await RunTest("FireAsync_5xxResponse_ReturnsRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "server error");
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireResult result = await client.FireAsync(MakeRequest());

                AssertEqual(FireOutcome.RetriableFailure, result.Outcome);
                AssertEqual(500, result.StatusCode);
                AssertNotNull(result.ErrorMessage);
                AssertContains("5xx", result.ErrorMessage!);
            });

            await RunTest("FireAsync_4xxResponse_ReturnsNonRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.BadRequest, "bad request");
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireResult result = await client.FireAsync(MakeRequest());

                AssertEqual(FireOutcome.NonRetriableFailure, result.Outcome);
                AssertEqual(400, result.StatusCode);
                AssertNotNull(result.ErrorMessage);
                AssertContains("4xx", result.ErrorMessage!);
            });

            await RunTest("FireAsync_401Response_ReturnsRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid token\"}");
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireResult result = await client.FireAsync(MakeRequest());

                AssertEqual(FireOutcome.RetriableFailure, result.Outcome);
                AssertEqual(401, result.StatusCode);
                AssertNotNull(result.ErrorMessage);
                AssertContains("auth failure", result.ErrorMessage!);
            });

            await RunTest("FireAsync_HeadersAndBody_AreCorrectlyShaped", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "{\"claude_code_session_url\":\"https://example.com\"}");
                HttpClient http = new HttpClient(handler);
                LoggingModule logging = new LoggingModule();
                RemoteTriggerHttpClient client = new RemoteTriggerHttpClient(logging, http);

                FireRequest req = new FireRequest
                {
                    FireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_test/fire",
                    BearerToken = "sk-ant-test-token",
                    BetaHeader = "experimental-cc-routine-2026-04-01",
                    AnthropicVersion = "2023-06-01",
                    Text = "hello from armada",
                };
                await client.FireAsync(req);

                AssertNotNull(handler.LastRequest);
                HttpRequestMessage recorded = handler.LastRequest!;

                AssertNotNull(recorded.Headers.Authorization);
                AssertEqual("Bearer", recorded.Headers.Authorization!.Scheme);
                AssertEqual("sk-ant-test-token", recorded.Headers.Authorization!.Parameter);

                AssertTrue(recorded.Headers.Contains("anthropic-beta"), "anthropic-beta header missing");
                AssertTrue(recorded.Headers.Contains("anthropic-version"), "anthropic-version header missing");

                AssertNotNull(handler.LastRequestBody);
                using JsonDocument doc = JsonDocument.Parse(handler.LastRequestBody!);
                AssertTrue(doc.RootElement.TryGetProperty("text", out JsonElement textEl), "body missing 'text' field");
                AssertEqual("hello from armada", textEl.GetString());
            });
        }

        private static FireRequest MakeRequest()
        {
            return new FireRequest
            {
                FireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_test/fire",
                BearerToken = "sk-ant-test-token",
                BetaHeader = "experimental-cc-routine-2026-04-01",
                AnthropicVersion = "2023-06-01",
                Text = "test event",
            };
        }

        private sealed class RecordingHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _StatusCode;
            private readonly string _ResponseBody;

            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestBody { get; private set; }

            public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
            {
                _StatusCode = statusCode;
                _ResponseBody = responseBody;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                if (request.Content != null)
                    LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                HttpResponseMessage response = new HttpResponseMessage(_StatusCode)
                {
                    Content = new StringContent(_ResponseBody, Encoding.UTF8, "application/json"),
                };
                return response;
            }
        }
    }
}
