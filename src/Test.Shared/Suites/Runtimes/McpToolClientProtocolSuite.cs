namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Runtimes.Mcp;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="McpToolClient"/> against a local HTTP server. An expired session is HTTP 404
    /// on a request that carried a session id: the client opens one new session with the same caller credential
    /// and sends that request once. A second 404, a timeout, a server error, a dropped connection, and a 404
    /// on a request that carried no session id stay failed.
    /// </summary>
    public sealed class McpToolClientProtocolSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.McpToolClientProtocol";
        private const string CallerToken = "caller-token";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the MCP tool client protocol suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("expired_session_replays_the_request_once", "A session-bearing HTTP 404 reopens the session and replays that request once", TestTags.Negative, async () =>
            {
                using LoopbackMcpServer server = await LoopbackMcpServer.StartAsync(request =>
                {
                    if (String.Equals(request.Method, "initialize", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(200, InitializeBody(), request.SeenInitialize == 1 ? "expired" : "fresh"));
                    if (String.Equals(request.Method, "tools/call", StringComparison.Ordinal) && String.Equals(request.SessionId, "expired", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(404, "session expired"));
                    if (String.Equals(request.Method, "tools/call", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(200, ToolBody("memory persisted")));
                    return Task.FromResult(LoopbackMcpServer.Json(200, "{}"));
                }).ConfigureAwait(false);

                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                await client.InitializeAsync().ConfigureAwait(false);
                McpToolCallResult result = await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);

                AssertEqual("memory persisted", result.Text);
                AssertEqual(2, server.Count("initialize"));
                AssertEqual(2, server.Count("tools/call"));
                AssertEqual("expired", server.Requests("tools/call")[0].SessionId);
                AssertEqual("fresh", server.Requests("tools/call")[1].SessionId);
                AssertTrue(String.IsNullOrEmpty(server.Requests("initialize")[1].SessionId), "The replacement initialize sends no expired session id.");
                foreach (LoopbackMcpServer.RecordedRequest request in server.Requests("initialize"))
                    AssertEqual(CallerToken, request.Token, "Recovery keeps the caller credential.");
            }));

            cases.Add(CaseAsync("second_session_not_found_is_not_replayed", "A second session HTTP 404 is returned after one replacement and one replay", TestTags.Negative, async () =>
            {
                using LoopbackMcpServer server = await LoopbackMcpServer.StartAsync(request =>
                {
                    if (String.Equals(request.Method, "initialize", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(200, InitializeBody(), "session-" + request.SeenInitialize));
                    if (String.Equals(request.Method, "tools/call", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(404, "session expired"));
                    return Task.FromResult(LoopbackMcpServer.Json(200, "{}"));
                }).ConfigureAwait(false);

                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                await client.InitializeAsync().ConfigureAwait(false);
                McpClientException? failure = null;
                try
                {
                    await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);
                }
                catch (McpClientException ex)
                {
                    failure = ex;
                }

                AssertEqual(404, failure?.StatusCode, "The replayed 404 reaches the caller.");
                AssertEqual(2, server.Count("initialize"));
                AssertEqual(2, server.Count("tools/call"));
            }));

            cases.Add(CaseAsync("not_found_without_a_session_id_is_not_replayed", "HTTP 404 on a request that carried no session id is not sent again", TestTags.Negative, async () =>
            {
                using LoopbackMcpServer server = await LoopbackMcpServer.StartAsync(request =>
                {
                    return Task.FromResult(LoopbackMcpServer.Json(404, "missing"));
                }).ConfigureAwait(false);

                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                McpClientException? failure = null;
                try
                {
                    await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);
                }
                catch (McpClientException ex)
                {
                    failure = ex;
                }

                AssertEqual(404, failure?.StatusCode);
                AssertEqual(1, server.Count("tools/call"));
                AssertEqual(0, server.Count("initialize"));
                AssertTrue(String.IsNullOrEmpty(server.Requests("tools/call")[0].SessionId), "The refused request carried no session id.");
            }));

            cases.Add(CaseAsync("server_error_is_not_replayed", "HTTP 503 is not sent again", TestTags.Negative, async () =>
            {
                using LoopbackMcpServer server = await LoopbackMcpServer.StartAsync(request =>
                {
                    if (String.Equals(request.Method, "initialize", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(200, InitializeBody(), "live"));
                    if (String.Equals(request.Method, "tools/call", StringComparison.Ordinal))
                        return Task.FromResult(LoopbackMcpServer.Json(503, "unavailable"));
                    return Task.FromResult(LoopbackMcpServer.Json(200, "{}"));
                }).ConfigureAwait(false);

                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                await client.InitializeAsync().ConfigureAwait(false);
                McpClientException? failure = null;
                try
                {
                    await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);
                }
                catch (McpClientException ex)
                {
                    failure = ex;
                }

                AssertEqual(503, failure?.StatusCode);
                AssertEqual(1, server.Count("initialize"));
                AssertEqual(1, server.Count("tools/call"));
            }));

            cases.Add(CaseAsync("connection_abort_is_not_replayed", "A dropped connection is not sent again", TestTags.Negative, async () =>
            {
                using RawDropServer server = RawDropServer.Start();
                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                await client.InitializeAsync().ConfigureAwait(false);
                Exception? failure = null;
                try
                {
                    await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not McpClientException)
                {
                    failure = ex;
                }

                AssertNotNull(failure, "The dropped connection must fail the call.");
                AssertEqual(1, server.InitializeCount);
                AssertEqual(1, server.CallCount);
            }));

            cases.Add(CaseAsync("timeout_is_not_replayed", "A request timeout is not sent again", TestTags.Negative, async () =>
            {
                TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using LoopbackMcpServer server = await LoopbackMcpServer.StartAsync(async request =>
                {
                    if (String.Equals(request.Method, "initialize", StringComparison.Ordinal))
                        return LoopbackMcpServer.Json(200, InitializeBody(), "live");
                    if (String.Equals(request.Method, "tools/call", StringComparison.Ordinal))
                    {
                        await release.Task.ConfigureAwait(false);
                        return LoopbackMcpServer.Json(200, ToolBody("late"));
                    }

                    return LoopbackMcpServer.Json(200, "{}");
                }).ConfigureAwait(false);

                using McpToolClient client = new McpToolClient(server.Endpoint, CallerToken, timeoutSeconds: 5);
                try
                {
                    await client.InitializeAsync().ConfigureAwait(false);
                    Exception? failure = null;
                    try
                    {
                        await client.CallToolAsync("write_memory", "{}").ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not McpClientException)
                    {
                        failure = ex;
                    }

                    AssertNotNull(failure, "The timed-out call must fail.");
                    AssertEqual(1, server.Count("initialize"));
                    AssertEqual(1, server.Count("tools/call"));
                }
                finally
                {
                    release.TrySetResult(true);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "MCP Tool Client Protocol",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string InitializeBody()
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\"}}";
        }

        private static string ToolBody(string text)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        /// <summary>
        /// Local HTTP server that speaks just enough MCP JSON for one client. Each test chooses the status,
        /// body, and session id of every request.
        /// </summary>
        private sealed class LoopbackMcpServer : IDisposable
        {
            private readonly HttpListener _Listener;
            private readonly CancellationTokenSource _Stop;
            private readonly Task _Listen;
            private readonly Func<RecordedRequest, Task<ServerReply>> _Respond;
            private readonly object _Gate = new object();
            private readonly List<RecordedRequest> _Requests = new List<RecordedRequest>();
            private readonly Dictionary<string, int> _Seen = new Dictionary<string, int>(StringComparer.Ordinal);

            private LoopbackMcpServer(HttpListener listener, Func<RecordedRequest, Task<ServerReply>> respond)
            {
                _Listener = listener;
                _Respond = respond;
                _Stop = new CancellationTokenSource();
                _Listen = Task.Run(ListenAsync);
            }

            public string Endpoint { get; private set; } = String.Empty;

            public static async Task<LoopbackMcpServer> StartAsync(Func<RecordedRequest, Task<ServerReply>> respond)
            {
                if (respond == null) throw new ArgumentNullException(nameof(respond));
                HttpListener? listener = null;
                int port = 0;
                await LoopbackPorts.StartAsync(() =>
                {
                    port = LoopbackPorts.FindFree();
                    listener = new HttpListener();
                    listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    listener.Start();
                    return Task.CompletedTask;
                }, () =>
                {
                    try { listener?.Close(); } catch { }
                    listener = null;
                }).ConfigureAwait(false);

                LoopbackMcpServer server = new LoopbackMcpServer(listener!, respond);
                server.Endpoint = "http://127.0.0.1:" + port + "/mcp";
                return server;
            }

            public int Count(string method)
            {
                lock (_Gate)
                {
                    return _Requests.FindAll(request => String.Equals(request.Method, method, StringComparison.Ordinal)).Count;
                }
            }

            public List<RecordedRequest> Requests(string method)
            {
                lock (_Gate)
                {
                    return _Requests.FindAll(request => String.Equals(request.Method, method, StringComparison.Ordinal));
                }
            }

            public void Dispose()
            {
                _Stop.Cancel();
                try { _Listener.Stop(); } catch { }
                try { _Listener.Close(); } catch { }
                try { _Listen.Wait(TimeSpan.FromSeconds(2)); } catch { }
            }

            public static ServerReply Json(int status, string body, string? sessionId = null)
            {
                return new ServerReply { Status = status, Body = body, SessionId = sessionId };
            }

            private async Task ListenAsync()
            {
                while (!_Stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _Listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleAsync(context));
                }
            }

            private async Task HandleAsync(HttpListenerContext context)
            {
                try
                {
                    string body;
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);

                    string method = ReadMethod(body);
                    int seen;
                    lock (_Gate)
                    {
                        _Seen.TryGetValue(method, out int current);
                        seen = current + 1;
                        _Seen[method] = seen;
                    }

                    RecordedRequest recorded = new RecordedRequest
                    {
                        Method = method,
                        SessionId = context.Request.Headers["Mcp-Session-Id"],
                        Token = context.Request.Headers[McpToolClient.SessionTokenHeader],
                        SeenInitialize = String.Equals(method, "initialize", StringComparison.Ordinal) ? seen : 0
                    };
                    lock (_Gate) _Requests.Add(recorded);

                    ServerReply reply = await _Respond(recorded).ConfigureAwait(false);
                    byte[] payload = Encoding.UTF8.GetBytes(reply.Body ?? String.Empty);
                    context.Response.StatusCode = reply.Status;
                    context.Response.ContentType = "application/json";
                    if (!String.IsNullOrEmpty(reply.SessionId))
                        context.Response.AddHeader("Mcp-Session-Id", reply.SessionId);
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                    context.Response.OutputStream.Close();
                }
                catch (Exception)
                {
                    try { context.Response.Abort(); } catch { }
                }
            }

            private static string ReadMethod(string body)
            {
                if (String.IsNullOrWhiteSpace(body)) return String.Empty;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("method", out JsonElement method))
                        return method.GetString() ?? String.Empty;
                }
                catch (JsonException)
                {
                }

                return String.Empty;
            }

            public sealed class RecordedRequest
            {
                public string Method { get; set; } = String.Empty;

                public string? SessionId { get; set; }

                public string? Token { get; set; }

                public int SeenInitialize { get; set; }
            }

            public sealed class ServerReply
            {
                public int Status { get; set; } = 200;

                public string Body { get; set; } = "{}";

                public string? SessionId { get; set; }
            }
        }

        /// <summary>
        /// Local TCP server. Initialize and the initialized notification get a normal HTTP response.
        /// A tool call is accepted and the socket is reset, which is a transport failure rather than an HTTP status.
        /// </summary>
        private sealed class RawDropServer : IDisposable
        {
            private readonly TcpListener _Listener;
            private readonly CancellationTokenSource _Stop = new CancellationTokenSource();
            private readonly Task _Accept;
            private int _InitializeCount;
            private int _CallCount;

            private RawDropServer(TcpListener listener)
            {
                _Listener = listener;
                _Accept = Task.Run(AcceptAsync);
            }

            public string Endpoint { get; private set; } = String.Empty;

            public int InitializeCount => Volatile.Read(ref _InitializeCount);

            public int CallCount => Volatile.Read(ref _CallCount);

            public static RawDropServer Start()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                RawDropServer server = new RawDropServer(listener);
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                server.Endpoint = "http://127.0.0.1:" + port + "/mcp";
                return server;
            }

            public void Dispose()
            {
                _Stop.Cancel();
                try { _Listener.Stop(); } catch (SocketException) { }
                try { _Accept.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            }

            private async Task AcceptAsync()
            {
                while (!_Stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _Listener.AcceptTcpClientAsync(_Stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    string request = await ReadRequestAsync(client.GetStream()).ConfigureAwait(false);
                    if (request.Contains("\"method\":\"tools/call\"", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _CallCount);
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Client.Close();
                        return;
                    }

                    string body = request.Contains("\"method\":\"initialize\"", StringComparison.Ordinal)
                        ? InitializeBody()
                        : "{}";
                    if (request.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
                        Interlocked.Increment(ref _InitializeCount);
                    string session = request.Contains("\"method\":\"initialize\"", StringComparison.Ordinal) ? "live" : null;
                    await WriteResponseAsync(client.GetStream(), body, session).ConfigureAwait(false);
                }
            }

            private static async Task<string> ReadRequestAsync(NetworkStream stream)
            {
                byte[] buffer = new byte[4096];
                MemoryStream data = new MemoryStream();
                int contentLength = 0;
                int headerLength = -1;
                while (headerLength < 0 || data.Length < headerLength + contentLength)
                {
                    int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0) break;
                    data.Write(buffer, 0, read);
                    if (headerLength >= 0) continue;

                    string soFar = Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length);
                    int end = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    headerLength = Encoding.UTF8.GetByteCount(soFar.Substring(0, end + 4));
                    contentLength = ReadContentLength(soFar.Substring(0, end));
                }

                return Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length);
            }

            private static int ReadContentLength(string headers)
            {
                foreach (string line in headers.Split("\r\n"))
                {
                    if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = line.Substring("Content-Length:".Length).Trim();
                    if (Int32.TryParse(value, out int length)) return length;
                }

                return 0;
            }

            private static async Task WriteResponseAsync(NetworkStream stream, string json, string? sessionId)
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                StringBuilder response = new StringBuilder();
                response.Append("HTTP/1.1 200 OK\r\n");
                response.Append("Content-Type: application/json\r\n");
                response.Append("Content-Length: ").Append(body.Length.ToString()).Append("\r\n");
                response.Append("Connection: close\r\n");
                if (!String.IsNullOrEmpty(sessionId))
                    response.Append("Mcp-Session-Id: ").Append(sessionId).Append("\r\n");
                response.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString())).ConfigureAwait(false);
                await stream.WriteAsync(body).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }

        #endregion
    }
}
