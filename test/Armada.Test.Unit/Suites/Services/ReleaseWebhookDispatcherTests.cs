namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for the CD release webhook HTTP dispatcher.
    /// </summary>
    public class ReleaseWebhookDispatcherTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Release Webhook Dispatcher";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("DispatchAsync_2xxResponse_ReturnsSuccess", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "{\"accepted\":true}");
                using HttpClient http = new HttpClient(handler);
                ReleaseWebhookDispatcher dispatcher = CreateDispatcher(http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.Success, result.Outcome);
                AssertEqual(200, result.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_5xxResponse_ReturnsRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.InternalServerError, "server error");
                using HttpClient http = new HttpClient(handler);
                ReleaseWebhookDispatcher dispatcher = CreateDispatcher(http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.RetriableFailure, result.Outcome);
                AssertEqual(500, result.StatusCode);
                AssertNotNull(result.ErrorMessage);
                AssertContains("5xx", result.ErrorMessage!);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_4xxResponse_ReturnsNonRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.BadRequest, "bad request");
                using HttpClient http = new HttpClient(handler);
                ReleaseWebhookDispatcher dispatcher = CreateDispatcher(http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.NonRetriableFailure, result.Outcome);
                AssertEqual(400, result.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_AuthFailure_ReturnsRetriableFailure", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid token\"}");
                using HttpClient http = new HttpClient(handler);
                CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook", BearerToken = "token-123" };
                ReleaseWebhookDispatcher dispatcher = new ReleaseWebhookDispatcher(settings, CreateLogging(), http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.RetriableFailure, result.Outcome);
                AssertContains("auth failure", result.ErrorMessage!);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_PayloadAndHeaders_AreCorrectlyShaped", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}");
                using HttpClient http = new HttpClient(handler);
                CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook", BearerToken = "token-123" };
                ReleaseWebhookDispatcher dispatcher = new ReleaseWebhookDispatcher(settings, CreateLogging(), http);

                await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                HttpRequestMessage recorded = handler.LastRequest!;
                AssertEqual(HttpMethod.Post, recorded.Method);
                AssertContains("https://cd.example.test/hook", recorded.RequestUri!.ToString());

                AssertNotNull(recorded.Headers.Authorization);
                AssertEqual("Bearer", recorded.Headers.Authorization!.Scheme);
                AssertEqual("token-123", recorded.Headers.Authorization!.Parameter);

                AssertNotNull(handler.LastRequestBody);
                using JsonDocument doc = JsonDocument.Parse(handler.LastRequestBody!);
                AssertTrue(doc.RootElement.TryGetProperty("event", out JsonElement eventEl), "body missing 'event' field");
                AssertEqual("release.shipped", eventEl.GetString());
                AssertTrue(doc.RootElement.TryGetProperty("releaseId", out JsonElement releaseEl), "body missing 'releaseId' field");
                AssertEqual("rel_test1", releaseEl.GetString());
                AssertTrue(doc.RootElement.TryGetProperty("version", out JsonElement versionEl), "body missing 'version' field");
                AssertEqual("1.2.3", versionEl.GetString());
                AssertTrue(doc.RootElement.TryGetProperty("missionIds", out JsonElement missionsEl), "body missing 'missionIds' field");
                AssertEqual(1, missionsEl.GetArrayLength());
            }).ConfigureAwait(false);

            await RunTest("Constructor_MissingUrl_Throws", () =>
            {
                CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = null };
                LoggingModule logging = CreateLogging();

                AssertThrows<ArgumentException>(() => new ReleaseWebhookDispatcher(settings, logging));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_RetriableThenSuccess_RetriesAndReturnsSuccess", async () =>
            {
                SequenceHttpMessageHandler handler = new SequenceHttpMessageHandler(new List<(HttpStatusCode, string)>
                {
                    (HttpStatusCode.InternalServerError, "first attempt fails"),
                    (HttpStatusCode.OK, "{}")
                });
                using HttpClient http = new HttpClient(handler);
                CdWebhookSettings settings = new CdWebhookSettings
                {
                    Enabled = true,
                    Url = "https://cd.example.test/hook",
                    MaxRetries = 2,
                    RetryBackoffSeconds = 1
                };
                ReleaseWebhookDispatcher dispatcher = new ReleaseWebhookDispatcher(settings, CreateLogging(), http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.Success, result.Outcome);
                AssertEqual(2, result.Attempts);
                AssertEqual(2, handler.RequestCount);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_NonRetriableFailure_DoesNotRetry", async () =>
            {
                SequenceHttpMessageHandler handler = new SequenceHttpMessageHandler(new List<(HttpStatusCode, string)>
                {
                    (HttpStatusCode.BadRequest, "bad request")
                });
                using HttpClient http = new HttpClient(handler);
                CdWebhookSettings settings = new CdWebhookSettings
                {
                    Enabled = true,
                    Url = "https://cd.example.test/hook",
                    MaxRetries = 3,
                    RetryBackoffSeconds = 1
                };
                ReleaseWebhookDispatcher dispatcher = new ReleaseWebhookDispatcher(settings, CreateLogging(), http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.NonRetriableFailure, result.Outcome);
                AssertEqual(1, result.Attempts);
                AssertEqual(1, handler.RequestCount);
            }).ConfigureAwait(false);

            await RunTest("DispatchAsync_AllRetriable_ExhaustsRetries", async () =>
            {
                SequenceHttpMessageHandler handler = new SequenceHttpMessageHandler(new List<(HttpStatusCode, string)>
                {
                    (HttpStatusCode.ServiceUnavailable, "down"),
                    (HttpStatusCode.ServiceUnavailable, "down"),
                    (HttpStatusCode.ServiceUnavailable, "down")
                });
                using HttpClient http = new HttpClient(handler);
                CdWebhookSettings settings = new CdWebhookSettings
                {
                    Enabled = true,
                    Url = "https://cd.example.test/hook",
                    MaxRetries = 2,
                    RetryBackoffSeconds = 1
                };
                ReleaseWebhookDispatcher dispatcher = new ReleaseWebhookDispatcher(settings, CreateLogging(), http);

                WebhookDispatchResult result = await dispatcher.DispatchAsync(MakePayload()).ConfigureAwait(false);

                AssertEqual(WebhookDispatchOutcome.RetriableFailure, result.Outcome);
                AssertEqual(3, result.Attempts);
                AssertEqual(3, handler.RequestCount);
            }).ConfigureAwait(false);

            await RunTest("Settings_MaxRetries_ClampedToValidRange", () =>
            {
                CdWebhookSettings tooLow = new CdWebhookSettings { MaxRetries = -4 };
                CdWebhookSettings tooHigh = new CdWebhookSettings { MaxRetries = 99 };

                AssertEqual(0, tooLow.MaxRetries);
                AssertEqual(5, tooHigh.MaxRetries);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static ReleaseWebhookDispatcher CreateDispatcher(HttpClient http)
        {
            CdWebhookSettings settings = new CdWebhookSettings { Enabled = true, Url = "https://cd.example.test/hook" };
            return new ReleaseWebhookDispatcher(settings, CreateLogging(), http);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ReleaseWebhookPayload MakePayload()
        {
            return new ReleaseWebhookPayload
            {
                ReleaseId = "rel_test1",
                VesselId = "vsl_test1",
                Title = "Test Release",
                Version = "1.2.3",
                TagName = "v1.2.3",
                Summary = "Unit test release",
                Status = "Shipped",
                MissionIds = new System.Collections.Generic.List<string> { "msn_test1" },
                PublishedUtc = DateTime.UtcNow
            };
        }

        private sealed class SequenceHttpMessageHandler : HttpMessageHandler
        {
            private readonly List<(HttpStatusCode StatusCode, string Body)> _Responses;
            private int _Index;

            public int RequestCount { get; private set; }

            public SequenceHttpMessageHandler(List<(HttpStatusCode, string)> responses)
            {
                _Responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount += 1;
                (HttpStatusCode statusCode, string body) = _Responses[Math.Min(_Index, _Responses.Count - 1)];
                _Index += 1;
                HttpResponseMessage response = new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(response);
            }
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
