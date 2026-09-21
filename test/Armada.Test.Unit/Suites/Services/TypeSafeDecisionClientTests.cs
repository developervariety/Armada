namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Text.Json.Nodes;
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

            await RunTest("Provenance hashes sent questions and retains only redacted definitions", async () =>
            {
                string fixtureKey = "sk-" + "fixture-secret";
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK,
                    "{\"model\":\"jev-1.13.0\",\"answers\":{\"q\":{\"type\":\"noul\",\"noul\":0.8}}}");
                using (HttpClient http = new HttpClient(handler))
                {
                    TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);
                    TypedDecisionRequest request = new TypedDecisionRequest
                    {
                        DecisionPoint = "captain_tool",
                        State = "fixture",
                        Questions = new Dictionary<string, TypedQuestion>
                        {
                            ["q"] = new NoulQuestion("QUESTION-SENTINEL " + fixtureKey, "supported", "unsupported")
                        }
                    };
                    TypedDecisionResult result = await client.DecideAsync(request, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(result.Available);
                    AssertNotNull(result.Provenance);
                    TypedDecisionProvenance provenance = result.Provenance!;
                    JsonObject sent = JsonSerializer.Deserialize<JsonObject>(handler.LastRequestBody!)!;
                    string wireQuestions = sent["questions"]!.ToJsonString();
                    AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wireQuestions))).ToLowerInvariant(), provenance.WireQuestionsSha256);
                    AssertEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handler.LastRequestBody!))).ToLowerInvariant(), provenance.RequestSha256);
                    AssertFalse(provenance.QuestionsJson.Contains(fixtureKey, StringComparison.Ordinal));
                    AssertContains("QUESTION-SENTINEL", provenance.QuestionsJson);
                    AssertContains("supported", provenance.QuestionsJson);
                    AssertContains("unsupported", provenance.QuestionsJson);
                    AssertEqual("jev-1.13.0", result.Model);
                    AssertFalse(JsonSerializer.Serialize(result).Contains("QUESTION-SENTINEL", StringComparison.Ordinal),
                        "tool/API serialization must not include host-local question definitions");
                }
            });

            await RunTest("DecideAsync_ScoreAnswerWithLegendObject_ParsesAndReportsModel", async () =>
            {
                // The provider returns a score legend as an index-keyed object, noul answers without a
                // confidence, and the concrete model version it ran.
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"model\":\"jev-1.13.0\",\"answers\":{" +
                    "\"cause\":{\"type\":\"choice\",\"choice\":\"work_defect\",\"confidence\":0.84," +
                    "\"probabilities\":{\"unclear\":0.11,\"work_defect\":0.89,\"environmental\":0.0}}," +
                    "\"repeat_likely\":{\"type\":\"noul\",\"noul\":0.53}," +
                    "\"sev\":{\"type\":\"score\",\"score\":1.71,\"confidence\":0.56," +
                    "\"legend\":{\"0\":\"Low\",\"1\":\"Medium\",\"2\":\"High\"}," +
                    "\"probabilities\":{\"0\":0.04,\"1\":0.21,\"2\":0.75}}}," +
                    "\"usage\":{\"input_tokens\":450,\"output_tokens\":80}}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);
                List<string> observed = new List<string>();
                client.ModelObserved = observed.Add;

                TypedDecisionRequest request = new TypedDecisionRequest
                {
                    DecisionPoint = "test",
                    State = "state",
                    Questions = new Dictionary<string, TypedQuestion>
                    {
                        ["cause"] = new ChoiceQuestion("Cause", new Dictionary<string, string> { ["unclear"] = "unclear", ["work_defect"] = "defect", ["environmental"] = "environment" }),
                        ["repeat_likely"] = new NoulQuestion("Will it recur"),
                        ["sev"] = new ScoreQuestion("Severity", new List<string> { "Low", "Medium", "High" })
                    }
                };
                TypedDecisionResult result = await client.DecideAsync(request, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Available, "a score answer with a legend object must parse, not return unavailable: " + result.UnavailableReason);
                AssertEqual(1, observed.Count, "the reported model version is passed to the observer");
                AssertEqual("jev-1.13.0", observed[0]);
                AssertEqual("jev-1.13.0", result.Model);
                AssertEqual(3, result.Answers.Count);
                AssertEqual("score", result.Answers["sev"].Type);
                AssertEqual(1.71, result.Answers["sev"].Score);
                AssertEqual(0.56, result.Answers["sev"].Confidence);
                AssertEqual(0.75, result.Answers["sev"].Probabilities!["2"]);
                AssertEqual(0.53, result.Answers["repeat_likely"].Noul);
                AssertNull(result.Answers["repeat_likely"].Confidence);
            });

            Dictionary<string, string> invalidResponses = new Dictionary<string, string>
            {
                ["missing_answers"] = "{}",
                ["empty_answers"] = "{\"answers\":{}}",
                ["partial_answers"] = "{\"answers\":{\"cause\":{\"type\":\"choice\",\"choice\":\"provider\",\"confidence\":0.9}}}",
                ["null_answer"] = "{\"answers\":{\"cause\":null,\"repeat_likely\":{\"type\":\"noul\",\"noul\":0.5}}}"
            };
            string validAnswers = "{\"cause\":{\"type\":\"choice\",\"choice\":\"provider\",\"confidence\":0.91,\"probabilities\":{\"provider\":0.8,\"environmental\":0.2}},\"repeat_likely\":{\"type\":\"noul\",\"noul\":0.7}}";
            invalidResponses["missing_type"] = "{\"answers\":" + validAnswers.Replace("\"type\":\"noul\",", "") + "}";
            invalidResponses["wrong_type"] = "{\"answers\":" + validAnswers.Replace("\"type\":\"noul\"", "\"type\":\"score\"") + "}";
            invalidResponses["missing_noul"] = "{\"answers\":" + validAnswers.Replace(",\"noul\":0.7", "") + "}";
            invalidResponses["noul_out_of_range"] = "{\"answers\":" + validAnswers.Replace("\"noul\":0.7", "\"noul\":1.1") + "}";
            invalidResponses["unknown_choice"] = "{\"answers\":" + validAnswers.Replace("\"choice\":\"provider\"", "\"choice\":\"unknown\"") + "}";
            invalidResponses["missing_confidence"] = "{\"answers\":" + validAnswers.Replace("\"confidence\":0.91,", "") + "}";
            invalidResponses["confidence_out_of_range"] = "{\"answers\":" + validAnswers.Replace("\"confidence\":0.91", "\"confidence\":-0.1") + "}";
            invalidResponses["probability_out_of_range"] = "{\"answers\":" + validAnswers.Replace("\"provider\":0.8", "\"provider\":1.8") + "}";
            invalidResponses["missing_probabilities"] = "{\"answers\":" + validAnswers.Replace(",\"probabilities\":{\"provider\":0.8,\"environmental\":0.2}", "") + "}";
            invalidResponses["unknown_probability_label"] = "{\"answers\":" + validAnswers.Replace("\"environmental\":0.2", "\"unknown\":0.2") + "}";
            foreach (KeyValuePair<string, string> invalid in invalidResponses)
            {
                await RunTest("DecideAsync_InvalidResponse_" + invalid.Key, async () =>
                {
                    using (HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, invalid.Value)))
                    {
                        TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);
                        TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);
                        AssertFalse(result.Available, "Invalid answers must not reach a decision gate");
                        AssertEqual("response_validation", result.UnavailableReason);
                        AssertEqual(0, result.Answers.Count, "Do not expose a partial result");
                    }
                });
            }

            await RunTest("DecideAsync_ScoreOutsideRubric_ReturnsUnavailable", async () =>
            {
                using (HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK,
                    "{\"answers\":{\"rating\":{\"type\":\"score\",\"score\":2.1,\"confidence\":0.9,\"probabilities\":{\"0\":0,\"1\":0,\"2\":1}}}}")))
                {
                    TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);
                    TypedDecisionResult result = await client.DecideAsync(new TypedDecisionRequest
                    {
                        DecisionPoint = "test",
                        State = "state",
                        Questions = new Dictionary<string, TypedQuestion> { ["rating"] = new ScoreQuestion("Rating", new List<string> { "Low", "Medium", "High" }) }
                    }, CancellationToken.None).ConfigureAwait(false);
                    AssertFalse(result.Available, "Score cannot exceed the rubric");
                    AssertEqual("response_validation", result.UnavailableReason);
                }
            });

            await RunTest("DecideAsync_ObjectState_SendsStateAsJsonObject", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.OK, "{\"answers\":{}}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);
                RedactedDecisionState state = DecisionStateRedactor.RedactState(
                    new Dictionary<string, object?> { ["failure_reason"] = "build failed", ["exit_code"] = 1 }, 8000);

                await client.DecideAsync(new TypedDecisionRequest
                {
                    DecisionPoint = "failure_cause",
                    State = state.State,
                    Questions = SampleRequest().Questions
                }, CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(handler.LastRequestBody);
                System.Text.Json.Nodes.JsonObject body = System.Text.Json.Nodes.JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
                AssertTrue(body["state"] is System.Text.Json.Nodes.JsonObject, "state must be a JSON object on the wire, not an encoded string");
                AssertEqual("build failed", body["state"]!["failure_reason"]!.GetValue<string>());
                AssertEqual(state.Text, body["state"]!.ToJsonString(), "the recorded text is exactly what was sent");
            });

            await RunTest("DecideAsync_401_ReturnsUnavailableHttp401", async () =>
            {
                await AssertUnavailable(HttpStatusCode.Unauthorized, "http_401").ConfigureAwait(false);
            });

            await RunTest("DecideAsync_422_ReturnsUnavailableHttp422", async () =>
            {
                await AssertUnavailable((HttpStatusCode)422, "http_422").ConfigureAwait(false);
            });

            await RunTest("DecideAsync_422ValidationBody_ReportsRedactedFieldDetail", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    (HttpStatusCode)422,
                    "{\"detail\":[{\"loc\":[\"body\",\"questions\",\"sev\",\"criteria\"],\"msg\":\"needs at least two levels\"," +
                    "\"input\":\"bearer-test-key /srv/private/checkout\"}]}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Available, "422 must be unavailable");
                AssertEqual("http_422", result.UnavailableReason);
                AssertEqual("body > questions > sev > criteria: needs at least two levels", result.UnavailableDetail);
            });

            await RunTest("DecideAsync_ErrorMessageBody_StripsKeyAndRedacts", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.BadRequest,
                    "{\"error\":\"bad key bearer-test-key for msn_abc123 at /srv/example/x\"}");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual("http_400", result.UnavailableReason);
                AssertNotNull(result.UnavailableDetail);
                AssertFalse(result.UnavailableDetail!.Contains("bearer-test-key", StringComparison.Ordinal), "the key must not survive");
                AssertFalse(result.UnavailableDetail.Contains("msn_abc123", StringComparison.Ordinal), "ids are redacted");
                AssertFalse(result.UnavailableDetail.Contains("/srv/", StringComparison.Ordinal), "paths are redacted");
            });

            await RunTest("DecideAsync_ErrorBodyNotJson_DetailIsNull", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
                HttpClient http = new HttpClient(handler);
                TypeSafeDecisionClient client = new TypeSafeDecisionClient(Settings(), new LoggingModule(), http);

                TypedDecisionResult result = await client.DecideAsync(SampleRequest(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual("http_502", result.UnavailableReason);
                AssertNull(result.UnavailableDetail);
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
                    "{\"answers\":{\"substantiated\":{\"type\":\"score\",\"score\":2.0,\"confidence\":0.7,\"probabilities\":{\"0\":0,\"1\":0,\"2\":1}}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");
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
