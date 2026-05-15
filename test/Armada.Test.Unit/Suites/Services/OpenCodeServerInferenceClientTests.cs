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
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests OpenCode server chat inference behavior.
    /// </summary>
    public class OpenCodeServerInferenceClientTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <inheritdoc />
        public override string Name => "OpenCode Server Inference Client";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CompleteAsync_HappyPath_ReturnsTextPart", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_123\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"step-start\"},{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"step-finish\"}]}");
                HttpClient http = new HttpClient(handler);
                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);

                string result = await client.CompleteAsync("system prompt", "user message").ConfigureAwait(false);

                AssertEqual("hello", result);
                AssertEqual(2, handler.Captures.Count, "Client should issue two POST requests.");
                AssertEqual("http://127.0.0.1:4096/session", handler.Captures[0].Uri);
                AssertEqual("http://127.0.0.1:4096/session/ses_123/message", handler.Captures[1].Uri);

                SessionRequestPayload? sessionBody = System.Text.Json.JsonSerializer.Deserialize<SessionRequestPayload>(handler.Captures[0].Body, _JsonOptions);
                AssertNotNull(sessionBody);
                AssertNotNull(sessionBody!.Model);
                AssertEqual("summary", sessionBody.Agent);
                AssertEqual("deepseek-v4-flash-free", sessionBody.Model!.Id);
                AssertEqual("opencode", sessionBody.Model.ProviderId);

                MessageRequestPayload? messageBody = System.Text.Json.JsonSerializer.Deserialize<MessageRequestPayload>(handler.Captures[1].Body, _JsonOptions);
                AssertNotNull(messageBody);
                AssertNotNull(messageBody!.Model);
                AssertNotNull(messageBody.Parts);
                AssertEqual("summary", messageBody.Agent);
                AssertEqual("opencode", messageBody.Model!.ProviderId);
                AssertEqual("deepseek-v4-flash-free", messageBody.Model.ModelId);
                AssertEqual("system prompt", messageBody.System);
                AssertEqual("text", messageBody.Parts![0].Type);
                AssertEqual("user message", messageBody.Parts[0].Text);
            });

            await RunTest("CompleteAsync_MultipleTextParts_ConcatenatesInOrder", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_abc\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"step-start\"},{\"type\":\"text\",\"text\":\"foo\"},{\"type\":\"text\",\"text\":\"bar\"},{\"type\":\"step-finish\"}]}");
                HttpClient http = new HttpClient(handler);
                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("foobar", result);
            });

            await RunTest("CompleteAsync_ReasoningPart_Ignored", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_abc\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"step-start\"},{\"type\":\"reasoning\",\"text\":\"thinking\"},{\"type\":\"text\",\"text\":\"answer\"},{\"type\":\"step-finish\"}]}");
                HttpClient http = new HttpClient(handler);
                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("answer", result);
            });

            await RunTest("CompleteAsync_MessageReturns500_ReturnsEmptyString", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_abc\"}");
                handler.Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"oops\"}");
                HttpClient http = new HttpClient(handler);
                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_ConnectionRefused_ReturnsEmptyString", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_abc\"}");
                handler.EnqueueThrow(new HttpRequestException("connection refused"));
                HttpClient http = new HttpClient(handler);
                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);

                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });
        }

        private static ArmadaSettings BuildSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.CodeIndex.InferenceClient = "OpenCodeServer";
            settings.CodeIndex.OpenCodeServer.BaseUrl = "http://127.0.0.1:4096";
            settings.CodeIndex.OpenCodeServer.ProviderId = "opencode";
            settings.CodeIndex.OpenCodeServer.ModelId = "deepseek-v4-flash-free";
            settings.CodeIndex.OpenCodeServer.Agent = "summary";
            settings.CodeIndex.OpenCodeServer.RequestTimeoutSeconds = 60;
            return settings;
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<ResponseScript> _Scripts = new Queue<ResponseScript>();

            public List<RequestCapture> Captures { get; } = new List<RequestCapture>();

            public void Enqueue(HttpStatusCode statusCode, string body)
            {
                ResponseScript script = new ResponseScript();
                script.StatusCode = statusCode;
                script.Body = body;
                _Scripts.Enqueue(script);
            }

            public void EnqueueThrow(Exception exception)
            {
                ResponseScript script = new ResponseScript();
                script.Exception = exception;
                _Scripts.Enqueue(script);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_Scripts.Count < 1) throw new InvalidOperationException("No scripted response available.");
                string requestBody = string.Empty;
                if (request.Content != null)
                    requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                RequestCapture capture = new RequestCapture();
                capture.Uri = request.RequestUri?.ToString() ?? string.Empty;
                capture.Method = request.Method.Method;
                capture.Body = requestBody;
                if (request.Headers.Authorization != null)
                    capture.Authorization = request.Headers.Authorization.Scheme + " " + request.Headers.Authorization.Parameter;
                Captures.Add(capture);

                ResponseScript next = _Scripts.Dequeue();
                if (next.Exception != null) throw next.Exception;
                HttpResponseMessage response = new HttpResponseMessage(next.StatusCode)
                {
                    Content = new StringContent(next.Body ?? string.Empty, Encoding.UTF8, "application/json")
                };
                return response;
            }
        }

        private sealed class RequestCapture
        {
            public string Uri { get; set; } = string.Empty;
            public string Method { get; set; } = string.Empty;
            public string Body { get; set; } = string.Empty;
            public string Authorization { get; set; } = string.Empty;
        }

        private sealed class ResponseScript
        {
            public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
            public string? Body { get; set; }
            public Exception? Exception { get; set; }
        }

        private sealed class SessionRequestPayload
        {
            public string Agent { get; set; } = string.Empty;
            public SessionModelPayload? Model { get; set; }
        }

        private sealed class SessionModelPayload
        {
            public string Id { get; set; } = string.Empty;
            public string ProviderId { get; set; } = string.Empty;
        }

        private sealed class MessageRequestPayload
        {
            public string Agent { get; set; } = string.Empty;
            public MessageModelPayload? Model { get; set; }
            public string System { get; set; } = string.Empty;
            public List<MessagePartPayload>? Parts { get; set; }
        }

        private sealed class MessageModelPayload
        {
            public string ProviderId { get; set; } = string.Empty;
            public string ModelId { get; set; } = string.Empty;
        }

        private sealed class MessagePartPayload
        {
            public string Type { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
        }
    }
}
