namespace Armada.Test.Unit.Suites.Services
{
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
    }
}
