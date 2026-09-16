namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    public class TypeSafeDecisionClientTests : TestSuite
    {
        public override string Name => "TypeSafe Decision Client";

        private const string _KeyEnv = "ARMADA_TYPESAFE_KEY_TEST";

        private static TypedDecisionSettings Settings()
        {
            return new TypedDecisionSettings
            {
                BaseUrl = "https://api.typesafe.example",
                Model = "jev-latest",
                ApiKeyEnv = _KeyEnv,
                TimeoutSeconds = 5
            };
        }

        private static TypedDecisionRequest SampleRequest()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>
            {
                ["cause"] = new ChoiceQuestion("Pick the cause", new Dictionary<string, string>
                {
                    ["environmental"] = "host or infra",
                    ["provider"] = "provider fault"
                }),
                ["repeat_likely"] = new NoulQuestion("Will it recur", "recurs", "one-off")
            };
            return new TypedDecisionRequest
            {
                DecisionPoint = "failure_cause",
                State = "redacted state text",
                Questions = questions
            };
        }

        protected override async Task RunTestsAsync()
        {
            Environment.SetEnvironmentVariable(_KeyEnv, "bearer-test-key");

            await RunTest("DecideAsync_2xxResponse_ParsesAnswersAndSendsExpectedRequest", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"model\":\"jev-latest\",\"answers\":{\"cause\":{\"type\":\"choice\",\"choice\":\"provider\"," +
                    "\"probabilities\":{\"provider\":0.8,\"environmental\":0.2},\"confidence\":0.91}," +
                    "\"repeat_likely\":{\"type\":\"noul\",\"noul\":0.7,\"confidence\":0.6}}," +
                    "\"usage\":{\"input_tokens\":123,\"output_tokens\":45}}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Available, "result should be available");
                AssertNull(result.UnavailableReason);
                AssertEqual(123, result.InputTokens);
                AssertEqual(45, result.OutputTokens);
                AssertEqual(2, result.Answers.Count);
                AssertEqual("choice", result.Answers["cause"].Type);
                AssertEqual("provider", result.Answers["cause"].Choice);
                AssertEqual(0.91, result.Answers["cause"].Confidence);
                AssertNotNull(result.Answers["cause"].Probabilities);
                AssertEqual("noul", result.Answers["repeat_likely"].Type);
                AssertEqual(0.7, result.Answers["repeat_likely"].Noul);

                // Request shape: endpoint, Bearer, body.
                AssertNotNull(handler.LastRequest);
                AssertEqual("https://api.typesafe.example/v1/systemone", handler.LastRequest!.RequestUri!.ToString());
                AssertNotNull(handler.LastRequest.Headers.Authorization);
                AssertEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
                AssertEqual("bearer-test-key", handler.LastRequest.Headers.Authorization!.Parameter);

                AssertNotNull(handler.LastRequestBody);
                WireRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<WireRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertEqual("jev-latest", body!.Model);
                AssertNotNull(body.Questions);
                AssertTrue(body.Questions!.ContainsKey("cause"), "questions contains cause");
                AssertEqual("choice", body.Questions["cause"].Type);
                AssertEqual("Pick the cause", body.Questions["cause"].Instructions);
                AssertEqual("noul", body.Questions["repeat_likely"].Type);
            });

            await RunTest("DecideAsync_401_ReturnsUnavailableHttp401", async () =>
            {
                await AssertUnavailable(HttpStatusCode.Unauthorized, "http_401").ConfigureAwait(false);
            });

            await RunTest("DecideAsync_422_ReturnsUnavailableHttp422", async () =>
            {
                await AssertUnavailable((HttpStatusCode)422, "http_422").ConfigureAwait(false);
            });

            await RunTest("DecideAsync_429_ReturnsUnavailableHttp429_NoRetry", async () =>
            {
                CountingHttpMessageHandler handler = new CountingHttpMessageHandler(HttpStatusCode.TooManyRequests, "{}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Available, "429 must be unavailable");
                AssertEqual("http_429", result.UnavailableReason);
                AssertEqual(1, handler.RequestCount, "no retries in the hot path");
            });

            await RunTest("DecideAsync_529_ReturnsUnavailableHttp529", async () =>
            {
                await AssertUnavailable((HttpStatusCode)529, "http_529").ConfigureAwait(false);
            });

            await RunTest("DecideAsync_MalformedJson_ReturnsUnavailableParse", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "not json at all");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Available, "malformed body must be unavailable");
                AssertEqual("parse", result.UnavailableReason);
            });

            await RunTest("DecideAsync_Timeout_ReturnsUnavailableTimeout_NeverThrows", async () =>
            {
                DelayHttpMessageHandler handler = new DelayHttpMessageHandler(TimeSpan.FromSeconds(30));
                HttpClient http = new HttpClient(handler);
                TypedDecisionSettings settings = Settings();
                settings.TimeoutSeconds = 1;
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(settings, new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Available, "a slow provider is unavailable, not late");
                AssertEqual("timeout", result.UnavailableReason);
            });

            await RunTest("DecideAsync_NetworkException_ReturnsUnavailableException_NeverThrows", async () =>
            {
                ThrowingHttpMessageHandler handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Available, "a thrown transport error must never reach the caller");
                AssertEqual("exception", result.UnavailableReason);
            });

            await RunTest("DecideAsync_CallerCancelled_ReturnsUnavailable_NeverThrows", async () =>
            {
                DelayHttpMessageHandler handler = new DelayHttpMessageHandler(TimeSpan.FromSeconds(30));
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), cts.Token).ConfigureAwait(false);

                AssertFalse(result.Available, "a cancelled caller gets an unavailable result, not an exception");
                AssertEqual("timeout", result.UnavailableReason);
            });

            await RunTest("DecideAsync_ScoreQuestion_SendsOrderedLevels", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK,
                    "{\"answers\":{\"substantiated\":{\"type\":\"score\",\"score\":2.0,\"confidence\":0.7}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
                HttpClient http = new HttpClient(handler);
                Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>
                {
                    ["substantiated"] = new ScoreQuestion("How substantiated", new List<string> { "asserted", "partly", "evidenced" })
                };
                TypedDecisionRequest request = new TypedDecisionRequest
                {
                    DecisionPoint = "review_substance",
                    State = "state",
                    Questions = questions
                };
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(request, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Available, "score response available");
                AssertEqual("score", result.Answers["substantiated"].Type);
                AssertEqual(2.0, result.Answers["substantiated"].Score);
                AssertNotNull(handler.LastRequestBody);
                AssertContains("\"score\"", handler.LastRequestBody!);
                AssertContains("asserted", handler.LastRequestBody!);
            });

            await RunTest("DecideAsync_NoKeyInEnv_OmitsAuthorizationHeader", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK,
                    "{\"answers\":{},\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}");
                HttpClient http = new HttpClient(handler);
                TypedDecisionSettings settings = Settings();
                settings.ApiKeyEnv = "ARMADA_TYPESAFE_KEY_TEST_ABSENT";
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(settings, new LoggingModule(), http);

                await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertNull(handler.LastRequest!.Headers.Authorization, "no key means no Authorization header");
            });

            await RunTest("Constructor_NullSettings_Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}"));
                    TypeSafeDecisionClient ignored = new TypeSafeDecisionClient(null!, new LoggingModule(), http);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("Constructor_NullHttpClient_Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    TypeSafeDecisionClient ignored = new TypeSafeDecisionClient(Settings(), new LoggingModule(), null!);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("NullTypedDecisionClient_ReturnsDisabled", async () =>
            {
                NullTypedDecisionClient client = new NullTypedDecisionClient();
                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);
                AssertFalse(result.Available, "null client is never available");
                AssertEqual("disabled", result.UnavailableReason);
            });

            Environment.SetEnvironmentVariable(_KeyEnv, null);
        }

        private async Task AssertUnavailable(HttpStatusCode status, string expectedReason)
        {
            RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(status, "{\"error\":\"x\"}");
            HttpClient http = new HttpClient(handler);
            TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

            TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

            AssertFalse(result.Available, expectedReason + " must be unavailable");
            AssertEqual(expectedReason, result.UnavailableReason);
        }

        private sealed class WireRequestBody
        {
            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("questions")]
            public Dictionary<string, WireQuestionBody>? Questions { get; set; }
        }

        private sealed class WireQuestionBody
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("instructions")]
            public string? Instructions { get; set; }
        }

        private sealed class RecordingHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _StatusCode;
            private readonly string _Body;

            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestBody { get; private set; }

            public RecordingHttpMessageHandler(HttpStatusCode statusCode, string body)
            {
                _StatusCode = statusCode;
                _Body = body;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                if (request.Content != null)
                    LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(_StatusCode)
                {
                    Content = new StringContent(_Body, Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class CountingHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _StatusCode;
            private readonly string _Body;

            public int RequestCount { get; private set; }

            public CountingHttpMessageHandler(HttpStatusCode statusCode, string body)
            {
                _StatusCode = statusCode;
                _Body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromResult(new HttpResponseMessage(_StatusCode)
                {
                    Content = new StringContent(_Body, Encoding.UTF8, "application/json")
                });
            }
        }

        private sealed class DelayHttpMessageHandler : HttpMessageHandler
        {
            private readonly TimeSpan _Delay;

            public DelayHttpMessageHandler(TimeSpan delay)
            {
                _Delay = delay;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(_Delay, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"answers\":{}}", Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Exception _Exception;

            public ThrowingHttpMessageHandler(Exception exception)
            {
                _Exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw _Exception;
            }
        }
    }
}
