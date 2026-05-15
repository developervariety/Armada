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
    /// Edge-case and negative-path coverage for OpenCodeServerInferenceClient. The happy paths
    /// (text concat, reasoning ignore, message 5xx, connection refused) are already pinned by the
    /// sibling OpenCodeServerInferenceClientTests suite. This suite adds:
    /// - constructor null-arg validation,
    /// - Basic auth header behavior (set/cleared, default username when blank),
    /// - session 5xx and missing-id branches,
    /// - message-response missing/empty parts,
    /// - cancellation propagation,
    /// - custom agent/provider/model overrides,
    /// - base-URL trailing-slash normalization,
    /// - URL-escaping of session id in the message endpoint.
    /// </summary>
    public class OpenCodeServerInferenceClientNegativePathTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <inheritdoc />
        public override string Name => "OpenCode Server Inference Client (negative paths)";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor_NullSettings_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new ScriptedHttpMessageHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerInferenceClient _ = new OpenCodeServerInferenceClient(null!, SilentLogging(), http);
                });
            });

            await RunTest("Constructor_NullLogging_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new ScriptedHttpMessageHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerInferenceClient _ = new OpenCodeServerInferenceClient(BuildSettings(), null!, http);
                });
            });

            await RunTest("Constructor_NullHttpClient_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerInferenceClient _ = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), null!);
                });
            });

            await RunTest("CompleteAsync_PasswordSet_AddsBasicAuthHeaderToBothCalls", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                ArmadaSettings settings = BuildSettings();
                settings.CodeIndex.OpenCodeServer.Username = "alice";
                settings.CodeIndex.OpenCodeServer.Password = "s3cret";

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(settings, SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("ok", result);
                AssertEqual(2, handler.Captures.Count);
                string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));
                AssertEqual(expected, handler.Captures[0].Authorization, "Session call must carry Basic auth.");
                AssertEqual(expected, handler.Captures[1].Authorization, "Message call must carry Basic auth.");
            });

            await RunTest("CompleteAsync_PasswordEmpty_NoAuthorizationHeader", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                ArmadaSettings settings = BuildSettings();
                settings.CodeIndex.OpenCodeServer.Username = "alice";
                settings.CodeIndex.OpenCodeServer.Password = string.Empty;

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(settings, SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("ok", result);
                AssertEqual(string.Empty, handler.Captures[0].Authorization, "No auth header expected when password is blank.");
                AssertEqual(string.Empty, handler.Captures[1].Authorization, "No auth header expected when password is blank.");
            });

            await RunTest("CompleteAsync_PasswordSetUsernameBlank_DefaultsUsernameToOpencode", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                ArmadaSettings settings = BuildSettings();
                settings.CodeIndex.OpenCodeServer.Username = "   ";
                settings.CodeIndex.OpenCodeServer.Password = "pw";

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(settings, SilentLogging(), http);
                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:pw"));
                AssertEqual(expected, handler.Captures[0].Authorization, "Blank username must default to 'opencode'.");
            });

            await RunTest("CompleteAsync_SessionReturns500_ReturnsEmptyAndDoesNotIssueMessage", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"oops\"}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
                AssertEqual(1, handler.Captures.Count, "Failed session must short-circuit before message POST.");
            });

            await RunTest("CompleteAsync_SessionResponseMissingId_ReturnsEmptyAndDoesNotIssueMessage", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
                AssertEqual(1, handler.Captures.Count, "Empty session id must short-circuit before message POST.");
            });

            await RunTest("CompleteAsync_MessagePartsNull_ReturnsEmptyString", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_MessagePartsEmptyArray_ReturnsEmptyString", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[]}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual(string.Empty, result);
            });

            await RunTest("CompleteAsync_PreCancelledToken_ThrowsOperationCanceledException", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await client.CompleteAsync("sys", "user", cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            });

            await RunTest("CompleteAsync_CustomAgentProviderModel_FlowThroughIntoBothPayloads", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                ArmadaSettings settings = BuildSettings();
                settings.CodeIndex.OpenCodeServer.Agent = "research";
                settings.CodeIndex.OpenCodeServer.ProviderId = "openrouter";
                settings.CodeIndex.OpenCodeServer.ModelId = "qwen-2.5-coder";

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(settings, SilentLogging(), http);
                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                SessionRequestPayload? sess = JsonSerializer.Deserialize<SessionRequestPayload>(handler.Captures[0].Body, _JsonOptions);
                AssertNotNull(sess);
                AssertNotNull(sess!.Model);
                AssertEqual("research", sess.Agent);
                AssertEqual("openrouter", sess.Model!.ProviderId);
                AssertEqual("qwen-2.5-coder", sess.Model.Id);

                MessageRequestPayload? msg = JsonSerializer.Deserialize<MessageRequestPayload>(handler.Captures[1].Body, _JsonOptions);
                AssertNotNull(msg);
                AssertNotNull(msg!.Model);
                AssertEqual("research", msg.Agent);
                AssertEqual("openrouter", msg.Model!.ProviderId);
                AssertEqual("qwen-2.5-coder", msg.Model.ModelId);
            });

            await RunTest("CompleteAsync_BaseUrlWithTrailingSlash_NormalizedInRequestUris", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                ArmadaSettings settings = BuildSettings();
                settings.CodeIndex.OpenCodeServer.BaseUrl = "  http://127.0.0.1:9999/  ";

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(settings, SilentLogging(), http);
                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("http://127.0.0.1:9999/session", handler.Captures[0].Uri);
                AssertEqual("http://127.0.0.1:9999/session/ses_1/message", handler.Captures[1].Uri);
            });

            await RunTest("CompleteAsync_SessionIdContainingSlash_EscapedSoItDoesNotShiftPathStructure", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses/evil\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertContains("ses%2Fevil/message", handler.Captures[1].Uri,
                    "Slashes in the session id must be percent-encoded so they cannot reshape the request path.");
                AssertFalse(handler.Captures[1].Uri.Contains("/ses/evil/"),
                    "Raw '/ses/evil/' must never appear in the message URI; that would be a path-injection bug.");
            });

            await RunTest("CompleteAsync_TextPartWithMixedCaseType_StillRecognizedAsText", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"TEXT\",\"text\":\"hello\"},{\"type\":\"Text\",\"text\":\"world\"}]}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                string result = await client.CompleteAsync("sys", "user").ConfigureAwait(false);

                AssertEqual("helloworld", result);
            });

            await RunTest("CompleteAsync_NullSystemAndUserMessages_ResultsInEmptyStringPayload", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"id\":\"ses_1\"}");
                handler.Enqueue(HttpStatusCode.OK, "{\"parts\":[{\"type\":\"text\",\"text\":\"ok\"}]}");
                HttpClient http = new HttpClient(handler);

                OpenCodeServerInferenceClient client = new OpenCodeServerInferenceClient(BuildSettings(), SilentLogging(), http);
                await client.CompleteAsync(null!, null!).ConfigureAwait(false);

                MessageRequestPayload? msg = JsonSerializer.Deserialize<MessageRequestPayload>(handler.Captures[1].Body, _JsonOptions);
                AssertNotNull(msg);
                AssertNotNull(msg!.Parts);
                AssertEqual(string.Empty, msg.System, "Null system prompt must serialize as empty string.");
                AssertEqual(string.Empty, msg.Parts![0].Text, "Null user message must serialize as empty string.");
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

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                if (_Scripts.Count < 1) throw new InvalidOperationException("No scripted response available.");
                ResponseScript next = _Scripts.Dequeue();
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
