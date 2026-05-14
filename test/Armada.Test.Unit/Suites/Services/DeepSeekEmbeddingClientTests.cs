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

    public class DeepSeekEmbeddingClientTests : TestSuite
    {
        public override string Name => "DeepSeek Embedding Client";

        protected override async Task RunTestsAsync()
        {
            await RunTest("EmbedAsync_2xxResponse_ReturnsEmbeddingAndSendsExpectedRequest", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[0.25,-0.5,1.5]}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingModel = "deepseek-embedding",
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "test-embedding-key"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello world").ConfigureAwait(false);

                AssertEqual(3, result.Length);
                AssertEqual(0.25f, result[0]);
                AssertEqual(-0.5f, result[1]);
                AssertEqual(1.5f, result[2]);

                AssertNotNull(handler.LastRequest);
                AssertEqual("https://api.deepseek.com/v1/embeddings", handler.LastRequest!.RequestUri!.ToString());
                AssertNotNull(handler.LastRequest.Headers.Authorization);
                AssertEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
                AssertEqual("test-embedding-key", handler.LastRequest.Headers.Authorization!.Parameter);

                AssertNotNull(handler.LastRequestBody);
                EmbeddingRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<EmbeddingRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertEqual("deepseek-embedding", body!.Model);
                AssertNotNull(body.Input);
                AssertEqual(1, body.Input!.Length);
                AssertEqual("hello world", body.Input[0]);
            });

            await RunTest("EmbedAsync_500Response_ReturnsEmptyArray", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.InternalServerError,
                    "{\"error\":\"server error\"}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "test-embedding-key"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello world").ConfigureAwait(false);

                AssertEqual(0, result.Length);
            });

            await RunTest("EmbedAsync_EmptyApiKey_OmitsAuthorizationHeader", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[1.0]}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = string.Empty
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertEqual(1, result.Length);
                AssertNotNull(handler.LastRequest);
                AssertNull(handler.LastRequest!.Headers.Authorization,
                    "Authorization header must be omitted when EmbeddingApiKey is empty");
            });

            await RunTest("EmbedAsync_WhitespaceApiKey_OmitsAuthorizationHeader", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[0.1]}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "   "
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertNull(handler.LastRequest!.Headers.Authorization,
                    "whitespace-only ApiKey must be treated as empty");
            });

            await RunTest("EmbedAsync_TrailingSlashBaseUrl_NormalizesEndpointPath", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[0.0]}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com/",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertNotNull(handler.LastRequest);
                AssertEqual("https://api.deepseek.com/v1/embeddings", handler.LastRequest!.RequestUri!.ToString());
            });

            await RunTest("EmbedAsync_NetworkException_ReturnsEmptyArray", async () =>
            {
                ThrowingHttpMessageHandler handler = new ThrowingHttpMessageHandler(
                    new HttpRequestException("connection refused"));
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertEqual(0, result.Length);
            });

            await RunTest("EmbedAsync_CancellationRequested_ThrowsOperationCanceled", async () =>
            {
                ThrowingHttpMessageHandler handler = new ThrowingHttpMessageHandler(
                    new OperationCanceledException("cancelled"));
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.EmbedAsync("hello", cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            });

            await RunTest("EmbedAsync_MissingDataField_ReturnsEmptyArray", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"object\":\"list\"}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertEqual(0, result.Length);
            });

            await RunTest("EmbedAsync_EmptyDataArray_ReturnsEmptyArray", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertEqual(0, result.Length);
            });

            await RunTest("EmbedAsync_MalformedJsonBody_ReturnsEmptyArray", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "this is not json {{");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                float[] result = await client.EmbedAsync("hello").ConfigureAwait(false);

                AssertEqual(0, result.Length);
            });

            await RunTest("EmbedAsync_NullInputText_SendsEmptyStringInRequestBody", async () =>
            {
                RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
                    HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[0.0]}]}");
                HttpClient http = new HttpClient(handler);
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiBaseUrl = "https://api.deepseek.com",
                    EmbeddingApiKey = "k"
                };
                DeepSeekEmbeddingClient client = new DeepSeekEmbeddingClient(settings, new LoggingModule(), http);

                await client.EmbedAsync(null!).ConfigureAwait(false);

                AssertNotNull(handler.LastRequestBody);
                EmbeddingRequestBody? body = System.Text.Json.JsonSerializer.Deserialize<EmbeddingRequestBody>(handler.LastRequestBody!);
                AssertNotNull(body);
                AssertNotNull(body!.Input);
                AssertEqual(1, body.Input!.Length);
                AssertEqual(string.Empty, body.Input[0]);
            });

            await RunTest("Constructor_NullSettings_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}"));
                    DeepSeekEmbeddingClient ignored = new DeepSeekEmbeddingClient(null!, new LoggingModule(), http);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("Constructor_NullLogging_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    HttpClient http = new HttpClient(new RecordingHttpMessageHandler(HttpStatusCode.OK, "{}"));
                    DeepSeekEmbeddingClient ignored = new DeepSeekEmbeddingClient(new CodeIndexSettings(), null!, http);
                    GC.KeepAlive(ignored);
                });
            });

            await RunTest("Constructor_NullHttpClient_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    DeepSeekEmbeddingClient ignored = new DeepSeekEmbeddingClient(new CodeIndexSettings(), new LoggingModule(), null!);
                    GC.KeepAlive(ignored);
                });
            });
        }

        private sealed class EmbeddingRequestBody
        {
            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("input")]
            public string[]? Input { get; set; }
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
