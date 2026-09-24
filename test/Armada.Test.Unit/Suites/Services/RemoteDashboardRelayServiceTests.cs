namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Concurrent;
    using System.Net;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    public class RemoteDashboardRelayServiceTests : TestSuite
    {
        public override string Name => "Remote Dashboard Relay Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("HandleAsync RemoteClose_SessionIsGoneWhenTheClosedEventIsPublished", async () =>
            {
                await using LoopbackRelayHost host = await LoopbackRelayHost.StartAsync().ConfigureAwait(false);
                RelayEventCollector collector = new RelayEventCollector();
                await using RemoteDashboardRelayService service = new RemoteDashboardRelayService(
                    CreateLogging(),
                    CreateSettings(host.Port),
                    collector.RecordAsync);

                // A proxy may react to the closed event at once. Sending from inside the publish call
                // is the earliest such reaction, so the relay must already have dropped the session.
                RemoteTunnelRequestResult? sentOnClosedEvent = null;
                collector.OnPublishAsync = async (method, payload) =>
                {
                    if (!String.Equals(method, "armada.ws.closed", StringComparison.OrdinalIgnoreCase)) return;
                    sentOnClosedEvent = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest(
                            "armada.ws.message",
                            new RemoteTunnelWebSocketMessage
                            {
                                ProxySocketId = "sock-race",
                                Data = "sent on the closed event"
                            }),
                        CancellationToken.None).ConfigureAwait(false);
                };

                RemoteTunnelRequestResult openResult = await service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest(
                        "armada.ws.open",
                        new RemoteTunnelWebSocketOpenRequest
                        {
                            ProxySocketId = "sock-race",
                            Path = "/ws"
                        }),
                    CancellationToken.None).ConfigureAwait(false);
                AssertEqual(200, openResult.StatusCode, "WebSocket open should succeed");

                await service.HandleAsync(
                    RemoteTunnelProtocol.CreateRequest(
                        "armada.ws.message",
                        new RemoteTunnelWebSocketMessage
                        {
                            ProxySocketId = "sock-race",
                            Data = "close-me"
                        }),
                    CancellationToken.None).ConfigureAwait(false);

                await collector.WaitForAsync<RemoteTunnelWebSocketCloseRequest>(
                    "armada.ws.closed",
                    payload => payload.ProxySocketId == "sock-race").ConfigureAwait(false);

                AssertNotNull(sentOnClosedEvent, "The closed-event reaction must have run");
                AssertEqual(404, sentOnClosedEvent!.StatusCode,
                    "A message sent when the closed event is published must find no session, not reach the closing socket");
            }).ConfigureAwait(false);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings(int port, string restHostname = "127.0.0.1")
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.Rest.Hostname = restHostname;
            settings.Rest.Ssl = false;
            settings.AdmiralPort = port;
            return settings;
        }

        private static int ReservePort()
        {
            using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class RelayEventCollector
        {
            private readonly ConcurrentQueue<(string Method, object? Payload)> _Events = new ConcurrentQueue<(string Method, object? Payload)>();
            private readonly object _SignalLock = new object();
            private TaskCompletionSource _Recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// Runs inside the publish call, before it returns to the relay, so a test can act at the
            /// exact moment the relay announces an event.
            /// </summary>
            public Func<string, object?, Task>? OnPublishAsync { get; set; }

            public async Task RecordAsync(string method, object? payload, CancellationToken token)
            {
                if (OnPublishAsync != null)
                {
                    await OnPublishAsync(method, payload).ConfigureAwait(false);
                }

                _Events.Enqueue((method, payload));
                TaskCompletionSource recorded;
                lock (_SignalLock)
                {
                    recorded = _Recorded;
                    _Recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                recorded.TrySetResult();
            }

            public async Task<T> WaitForAsync<T>(string method, Func<T, bool> predicate, int timeoutMs = 5000) where T : class
            {
                // Wake on each recorded event rather than on a polling interval; the delay is only the
                // failure backstop for an event that never arrives.
                Task timeout = Task.Delay(timeoutMs);
                while (true)
                {
                    Task nextEvent;
                    lock (_SignalLock)
                    {
                        nextEvent = _Recorded.Task;
                    }

                    foreach ((string Method, object? Payload) entry in _Events)
                    {
                        if (!String.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (entry.Payload is T typed && predicate(typed))
                        {
                            return typed;
                        }
                    }

                    if (await Task.WhenAny(nextEvent, timeout).ConfigureAwait(false) == timeout)
                    {
                        throw new TimeoutException("Timed out waiting for relay event " + method + ".");
                    }
                }
            }
        }

        private sealed class LoopbackRelayHost : IAsyncDisposable
        {
            private readonly HttpListener _Listener;
            private readonly CancellationTokenSource _Cancellation = new CancellationTokenSource();
            private readonly Task _LoopTask;

            private LoopbackRelayHost(int port)
            {
                Port = port;
                _Listener = new HttpListener();
                _Listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _Listener.Start();
                _LoopTask = Task.Run(() => ListenAsync(_Cancellation.Token));
            }

            public int Port { get; }

            public static Task<LoopbackRelayHost> StartAsync()
            {
                return Task.FromResult(new LoopbackRelayHost(ReservePort()));
            }

            public async ValueTask DisposeAsync()
            {
                _Cancellation.Cancel();

                try
                {
                    _Listener.Stop();
                }
                catch
                {
                }

                try
                {
                    await _LoopTask.ConfigureAwait(false);
                }
                catch
                {
                }

                _Listener.Close();
                _Cancellation.Dispose();
            }

            private async Task ListenAsync(CancellationToken token)
            {
                while (!token.IsCancellationRequested)
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

                    _ = Task.Run(() => HandleContextAsync(context, token), CancellationToken.None);
                }
            }

            private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
            {
                if (context.Request.IsWebSocketRequest &&
                    String.Equals(context.Request.Url?.AbsolutePath, "/ws", StringComparison.OrdinalIgnoreCase))
                {
                    HttpListenerWebSocketContext socketContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                    await HandleWebSocketAsync(socketContext.WebSocket, token).ConfigureAwait(false);
                    return;
                }

                string path = context.Request.Url?.AbsolutePath ?? "/";
                switch (path)
                {
                    case "/api/v1/echo":
                        await HandleEchoAsync(context, token).ConfigureAwait(false);
                        return;
                    case "/api/v1/binary":
                        await HandleBinaryAsync(context, token).ConfigureAwait(false);
                        return;
                    case "/api/v1/delete-target":
                        context.Response.StatusCode = 204;
                        context.Response.Close();
                        return;
                    case "/api/v1/error":
                        await WriteResponseAsync(context, 502, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("upstream failed"), token).ConfigureAwait(false);
                        return;
                    case "/api/v1/large-response":
                        await WriteResponseAsync(
                            context,
                            200,
                            "application/octet-stream",
                            new byte[Constants.DefaultRemoteRelayMaxBodyBytes + 1],
                            token).ConfigureAwait(false);
                        return;
                    default:
                        await WriteResponseAsync(context, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"), token).ConfigureAwait(false);
                        return;
                }
            }

            private static async Task HandleEchoAsync(HttpListenerContext context, CancellationToken token)
            {
                byte[] bodyBytes = await ReadBodyAsync(context.Request.InputStream, token).ConfigureAwait(false);
                object payload = new
                {
                    method = context.Request.HttpMethod,
                    path = context.Request.Url?.AbsolutePath,
                    query = context.Request.Url?.Query?.TrimStart('?') ?? String.Empty,
                    header = context.Request.Headers["X-Test-Header"],
                    contentType = context.Request.ContentType,
                    bodyText = bodyBytes.Length > 0 ? Encoding.UTF8.GetString(bodyBytes) : null
                };

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, RemoteTunnelProtocol.JsonOptions);
                int statusCode = String.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ? 201 : 200;
                await WriteResponseAsync(context, statusCode, "application/json; charset=utf-8", json, token).ConfigureAwait(false);
            }

            private static async Task HandleBinaryAsync(HttpListenerContext context, CancellationToken token)
            {
                byte[] bodyBytes = await ReadBodyAsync(context.Request.InputStream, token).ConfigureAwait(false);
                await WriteResponseAsync(context, 200, "application/octet-stream", bodyBytes, token).ConfigureAwait(false);
            }

            private static async Task HandleWebSocketAsync(WebSocket socket, CancellationToken token)
            {
                byte[] buffer = new byte[4096];
                try
                {
                    while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                    {
                        using MemoryStream stream = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "remote close", token).ConfigureAwait(false);
                                return;
                            }

                            if (result.Count > 0)
                            {
                                stream.Write(buffer, 0, result.Count);
                            }
                        }
                        while (!result.EndOfMessage);

                        string message = Encoding.UTF8.GetString(stream.ToArray());
                        if (String.Equals(message, "close-me", StringComparison.Ordinal))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "remote close", token).ConfigureAwait(false);
                            return;
                        }

                        byte[] responseBytes = Encoding.UTF8.GetBytes("echo:" + message);
                        await socket.SendAsync(
                            new ArraySegment<byte>(responseBytes),
                            WebSocketMessageType.Text,
                            true,
                            token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    try
                    {
                        socket.Abort();
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    socket.Dispose();
                }
            }

            private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken token)
            {
                using MemoryStream buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
                return buffer.ToArray();
            }

            private static async Task WriteResponseAsync(HttpListenerContext context, int statusCode, string contentType, byte[] body, CancellationToken token)
            {
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = body.LongLength;
                if (body.Length > 0)
                {
                    await context.Response.OutputStream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
                }

                context.Response.Close();
            }
        }
    }
}
