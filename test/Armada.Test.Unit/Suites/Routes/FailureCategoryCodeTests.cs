namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Net.WebSockets;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Routes;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// An unhandled WebSocket command failure and a REST status the framework error set does not name both carry a
    /// machine-readable category that matches the failure. The existing <c>Error</c> value stays.
    /// </summary>
    public class FailureCategoryCodeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Failure Category Codes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildContext_WithoutContextService_ReturnsNotImplementedBesideBadRequest", async () =>
            {
                using (FailureHost host = FailureHost.Start())
                {
                    HttpResponseMessage response = await host.Client.PostAsync(
                        "/api/v1/vessels/vsl_missing/build-context",
                        new StringContent("{}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotImplemented, response.StatusCode, "model context without a context service is 501: " + body);
                    AssertEqual("BadRequest", ReadString(body, "Error"), "the existing error value stays BadRequest: " + body);
                    AssertEqual("NotImplemented", ReadString(body, "Code"), "the added code matches HTTP 501: " + body);
                    AssertTrue(body.Contains("Model Context building is not available", StringComparison.Ordinal), "the message stays: " + body);
                }
            }).ConfigureAwait(false);

            await RunTest("RepositoryHealth_WithoutHealthService_ReturnsServiceUnavailableBesideBadRequest", async () =>
            {
                using (FailureHost host = FailureHost.Start())
                {
                    HttpResponseMessage response = await host.Client.GetAsync("/api/v1/vessels/vsl_missing/repository-health").ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, "health without a health service is 503: " + body);
                    AssertEqual("BadRequest", ReadString(body, "Error"), "the existing error value stays BadRequest: " + body);
                    AssertEqual("ServiceUnavailable", ReadString(body, "Code"), "the added code matches HTTP 503: " + body);
                }
            }).ConfigureAwait(false);

            await RunTest("CheckoutImport_WithoutImportService_KeepsInternalErrorAndNamesServiceUnavailable", async () =>
            {
                using (FailureHost host = FailureHost.Start())
                {
                    HttpResponseMessage response = await host.Client.GetAsync("/api/v1/vessels/local-checkouts").ConfigureAwait(false);
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, "import without a service is 503: " + body);
                    AssertEqual("InternalError", ReadString(body, "Error"), "the existing error value stays InternalError: " + body);
                    AssertEqual("ServiceUnavailable", ReadString(body, "Code"), "the added code matches HTTP 503: " + body);
                }
            }).ConfigureAwait(false);

            await RunTest("Command_InvalidJson_ReturnsInvalidArgument", async () =>
            {
                using (FailureHost host = FailureHost.Start())
                {
                    string frame = await host.CommandFrameAsync("{\"route\":\"command\",\"action\":\"get_fleet\",\"lines\":\"nope\"}").ConfigureAwait(false);
                    AssertEqual("command.error", ReadString(frame, "type"), "invalid command JSON is a command error: " + frame);
                    AssertEqual("invalid_argument", ReadString(frame, "code"), "invalid command JSON maps to invalid_argument: " + frame);
                    AssertTrue(ReadString(frame, "error").Length > 0, "the error text stays: " + frame);
                }
            }).ConfigureAwait(false);

            await RunTest("CreateFleet_UndeserializableData_ReturnsInvalidArgument", async () =>
            {
                using (FailureHost host = FailureHost.Start())
                {
                    string frame = await host.CommandFrameAsync("{\"route\":\"command\",\"action\":\"create_fleet\",\"data\":\"not-a-fleet\"}").ConfigureAwait(false);
                    AssertEqual("command.error", ReadString(frame, "type"), "a command that throws is a command error: " + frame);
                    AssertEqual("invalid_argument", ReadString(frame, "code"), "a command whose data cannot be read maps to invalid_argument: " + frame);
                    AssertTrue(ReadString(frame, "error").Length > 0, "the error text stays: " + frame);
                }
            }).ConfigureAwait(false);
        }

        private static string ReadString(string json, string name)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                throw new Exception("JSON has no string '" + name + "': " + json);
            return value.GetString() ?? "";
        }

        /// <summary>
        /// A loopback server with the vessel and checkout routes, and the WebSocket hub, and no optional context,
        /// health, or import service.
        /// </summary>
        private sealed class FailureHost : IDisposable
        {
            private readonly TestDatabase _Database;
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            /// <summary>Client bound to this host, sending the administrator API key.</summary>
            public HttpClient Client { get; }

            private readonly string _ApiKey;
            private readonly int _Port;

            private FailureHost(TestDatabase database, Webserver server, CancellationTokenSource cancellation, HttpClient client, string apiKey, int port)
            {
                _Database = database;
                _Server = server;
                _Cancellation = cancellation;
                Client = client;
                _ApiKey = apiKey;
                _Port = port;
            }

            /// <summary>Start the host.</summary>
            /// <returns>The running host.</returns>
            public static FailureHost Start()
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                TestDatabase database = TestDatabaseHelper.CreateDatabaseAsync().GetAwaiter().GetResult();
                try
                {
                    string apiKey = "failure-key-" + Guid.NewGuid().ToString("N");
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.ApiKey = apiKey;
                    JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    AuthenticationService authentication = new AuthenticationService(database.Driver, new SessionTokenService(), settings, logging);
                    WorkflowProfileService profiles = new WorkflowProfileService(database.Driver, logging);
                    VesselRoutes vessels = new VesselRoutes(
                        database.Driver,
                        new VesselReadinessService(database.Driver, profiles, logging),
                        new LandingPreviewService(database.Driver, logging),
                        (_, _, _, _, _, _, _, _) => Task.CompletedTask,
                        jsonOptions);
                    IMergeQueueService mergeQueue = DispatchProxy.Create<IMergeQueueService, IdleMergeQueue>();
                    ArmadaWebSocketHub hub = new ArmadaWebSocketHub(
                        logging,
                        RecordingAdmiral.Create().Service,
                        database.Driver,
                        mergeQueue,
                        authentication,
                        settings);

                    int port = ReservePort();
                    WebserverSettings webserverSettings = new WebserverSettings();
                    webserverSettings.Hostname = "127.0.0.1";
                    webserverSettings.Port = port;
                    webserverSettings.WebSockets.Enable = true;
                    Webserver server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
                    {
                        ctx.Response.StatusCode = 404;
                        await ctx.Response.Send().ConfigureAwait(false);
                    });
                    Func<HttpContextBase, Task<AuthContext>> authenticate = ctx => authentication.AuthenticateAsync(
                        ctx.Request.Headers.Get("Authorization"),
                        ctx.Request.Headers.Get("X-Token"),
                        ctx.Request.Headers.Get("X-Api-Key"));
                    AuthorizationService authorization = new AuthorizationService();
                    new VesselCheckoutImportRoutes(null).Register(server, authenticate, authorization);
                    vessels.Register(server, authenticate, authorization);
                    server.WebSocket("/ws", hub.HandleWebSocketAsync);

                    CancellationTokenSource cancellation = new CancellationTokenSource();
                    server.Start(cancellation.Token);
                    HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(10) };
                    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
                    return new FailureHost(database, server, cancellation, client, apiKey, port);
                }
                catch
                {
                    database.Dispose();
                    throw;
                }
            }

            /// <summary>Authenticate a WebSocket session and return the frame for one command body.</summary>
            /// <param name="commandJson">Raw command message.</param>
            /// <returns>The command reply frame.</returns>
            public async Task<string> CommandFrameAsync(string commandJson)
            {
                using ClientWebSocket socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + _Port + "/ws"), CancellationToken.None).ConfigureAwait(false);
                await SendAsync(socket, "{\"route\":\"authenticate\",\"apiKey\":\"" + _ApiKey + "\"}").ConfigureAwait(false);
                string auth = await ReceiveAsync(socket).ConfigureAwait(false);
                if (ReadString(auth, "type") != "auth.result")
                    throw new Exception("WebSocket authentication failed: " + auth);
                await SendAsync(socket, commandJson).ConfigureAwait(false);
                return await ReceiveAsync(socket).ConfigureAwait(false);
            }

            /// <summary>Dispose the host.</summary>
            public void Dispose()
            {
                Client.Dispose();
                _Cancellation.Cancel();
                try { _Server.Stop(); }
                catch (Exception exception) { Console.WriteLine("Failure category test server stop failed: " + exception.Message); }
                _Server.Dispose();
                _Cancellation.Dispose();
                _Database.Dispose();
            }

            private static async Task SendAsync(ClientWebSocket socket, string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }

            private static async Task<string> ReceiveAsync(ClientWebSocket socket)
            {
                byte[] buffer = new byte[65536];
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                return Encoding.UTF8.GetString(buffer, 0, result.Count);
            }

            private static int ReservePort()
            {
                using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                    return port;
                }
            }
        }

        private class IdleMergeQueue : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                throw new InvalidOperationException("This test does not call the merge queue.");
            }
        }
    }
}
