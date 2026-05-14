namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    public class DeepSeekInferenceClientTests : TestSuite
    {
        public override string Name => "DeepSeek Inference Client";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CompleteAsync_2xxResponse_ReturnsCompletionAndSendsExpectedRequest", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"summary text\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerModel = "deepseek-chat",
                    SummarizerApiKey = "test-inference-key",
                    MaxSummaryOutputTokens = 1234
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("system prompt", "user message").ConfigureAwait(false);

                AssertEqual("summary text", result);

                AssertNotNull(handler.LastRequest);
                AssertEqual("https://api.deepseek.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
                AssertNotNull(handler.LastRequest.Headers.Authorization);
                AssertEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
                AssertEqual("test-inference-key", handler.LastRequest.Headers.Authorization!.Parameter);

                AssertNotNull(handler.LastRequestBody);
                CompletionRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<CompletionRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertEqual("deepseek-chat", body!.Model);
                AssertEqual(1234, body.MaxTokens);
                AssertNotNull(body.Messages);
                AssertEqual(2, body.Messages!.Length);
                AssertEqual("system", body.Messages[0].Role);
                AssertEqual("system prompt", body.Messages[0].Content);
                AssertEqual("user", body.Messages[1].Role);
                AssertEqual("user message", body.Messages[1].Content);
            });

            await RunTest("CompleteAsync_500Response_ReturnsEmptyString", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.InternalServerError,
                    "{\"error\":\"server error\"}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "test-inference-key"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("system prompt", "user message").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_EmptySummarizerBaseUrl_FallsBackToEmbeddingBaseUrl", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://embed.example.com",
                    SummarizerApiBaseUrl = string.Empty,
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertEqual("https://embed.example.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
            });

            await RunTest("CompleteAsync_EmptySummarizerApiKey_FallsBackToEmbeddingApiKey", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "embed-fallback-key",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = string.Empty
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertNotNull(handler.LastRequest!.Headers.Authorization);
                AssertEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
                AssertEqual("embed-fallback-key", handler.LastRequest.Headers.Authorization!.Parameter);
            });

            await RunTest("CompleteAsync_BothApiKeysEmpty_OmitsAuthorizationHeader", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = string.Empty,
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = string.Empty
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertNull(handler.LastRequest!.Headers.Authorization,
                    "Authorization header must be omitted when both summarizer and embedding keys are empty");
            });

            await RunTest("CompleteAsync_TrailingSlashBaseUrl_NormalizesEndpointPath", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com/",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertEqual("https://api.deepseek.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
            });

            await RunTest("CompleteAsync_NetworkException_ReturnsEmptyString", async () =>
            {
                ThrowingHttpMessageHandler handler = new ThrowingHttpMessageHandler(
                    new HttpRequestException("connection refused"));
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_CancellationRequested_ThrowsOperationCanceled", async () =>
            {
                ThrowingHttpMessageHandler handler = new ThrowingHttpMessageHandler(
                    new OperationCanceledException("cancelled"));
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.CompleteAsync("sys", "user", cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            });

            await RunTest("CompleteAsync_MissingChoicesField_ReturnsEmptyString", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"id\":\"resp-1\"}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_EmptyChoicesArray_ReturnsEmptyString", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_MalformedJsonBody_ReturnsEmptyString", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "not even close to json");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_NullPromptArguments_SendEmptyStringContent", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k"
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync(null!, null!).ConfigureAwait(false);

                AssertNotNull(handler.LastRequestBody);
                CompletionRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<CompletionRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertNotNull(body!.Messages);
                AssertEqual(2, body.Messages!.Length);
                AssertEqual("system", body.Messages[0].Role);
                AssertEqual(string.Empty, body.Messages[0].Content);
                AssertEqual("user", body.Messages[1].Role);
                AssertEqual(string.Empty, body.Messages[1].Content);
            });

            await RunTest("CompleteAsync_UsesMaxSummaryOutputTokensFromSettings", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiBaseUrl = "https://api.deepseek.com",
                    SummarizerApiKey = "k",
                    MaxSummaryOutputTokens = 4096
                };
                DeepSeekInferenceClient client = new DeepSeekInferenceClient(settings, new LoggingModule(), http);

                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertNotNull(handler.LastRequestBody);
                CompletionRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<CompletionRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertEqual(4096, body!.MaxTokens);
            });

            await RunTest("Constructor_NullSettings_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}"));
                    DeepSeekInferenceClient ignored = new DeepSeekInferenceClient(null!, new LoggingModule(), http);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("Constructor_NullLogging_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}"));
                    DeepSeekInferenceClient ignored = new DeepSeekInferenceClient(new CodeIndexSettings(), null!, http);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("Constructor_NullHttpClient_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    DeepSeekInferenceClient ignored = new DeepSeekInferenceClient(new CodeIndexSettings(), new LoggingModule(), null!);
                    GC.KeepAlive(ignored);
                });
            });
        }

        private sealed class CompletionRequestBody
        {
            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("messages")]
            public CompletionMessage[]? Messages { get; set; }

            [JsonPropertyName("max_tokens")]
            public int MaxTokens { get; set; }
        }

        private sealed class CompletionMessage
        {
            [JsonPropertyName("role")]
            public string? Role { get; set; }

            [JsonPropertyName("content")]
            public string? Content { get; set; }
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
                HttpResponseMessage response = new HttpResponseMessage(_StatusCode)
                {
                    Content = new StringContent(_Body, Encoding.UTF8, "application/json")
                };
                return response;
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
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                throw _Exception;
            }
        }
    }
}
