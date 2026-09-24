namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// HTTP tests for the in-place restart route (POST /api/v1/server/restart) registered by
    /// <see cref="StatusRoutes"/>, and its shutdown sibling. Covers: the auth guard honours
    /// RequireAuthForShutdown, the route returns "restarting", and it triggers the stop callback.
    /// Production runs under a container restart policy, so the graceful stop the callback performs is
    /// the restart; there is no separate relaunch to assert here.
    /// </summary>
    public class ServerRestartRouteTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Server Restart Route";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Restart_RequiresAuth_WhenRequireAuthForShutdown", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.RequireAuthForShutdown = true;
                    using (StatusRouteHost host = StatusRouteHost.Start(testDb, settings, () => { }))
                    {
                        HttpResponseMessage response = await host.Client.PostAsync("/api/v1/server/restart", null).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode, "Unauthenticated restart must be rejected with 401");
                    }
                }
            });

            await RunTest("ShutdownRoutes_RequireAuthUnderDefaultSettings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    AssertTrue(settings.RequireAuthForShutdown, "Shutdown requires authentication unless a deployment turns it off");
                    bool stopped = false;
                    using (StatusRouteHost host = StatusRouteHost.Start(testDb, settings, () => stopped = true))
                    {
                        HttpResponseMessage stop = await host.Client.PostAsync("/api/v1/server/stop", null).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Unauthorized, stop.StatusCode, "An unauthenticated stop must be rejected under default settings");
                        HttpResponseMessage restart = await host.Client.PostAsync("/api/v1/server/restart", null).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Unauthorized, restart.StatusCode, "An unauthenticated restart must be rejected under default settings");
                        await Task.Delay(700).ConfigureAwait(false);
                        AssertFalse(stopped, "A rejected request must not stop the server");
                    }
                }
            });

            await RunTest("Restart_ReturnsRestarting_AndTriggersStopCallback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.RequireAuthForShutdown = false;
                    TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (StatusRouteHost host = StatusRouteHost.Start(testDb, settings, () => stopped.TrySetResult(true)))
                    {
                        HttpResponseMessage response = await host.Client.PostAsync("/api/v1/server/restart", null).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.OK, response.StatusCode, "Restart must return 200 when auth is not required");
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertContains("restarting", body, "Restart response must report the restarting status");

                        Task completed = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        AssertTrue(completed == stopped.Task, "Restart must trigger the graceful stop callback");
                    }
                }
            });

            await RunTest("Restart_NoAuthResponse_DoesNotReportShuttingDown", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.RequireAuthForShutdown = false;
                    using (StatusRouteHost host = StatusRouteHost.Start(testDb, settings, () => { }))
                    {
                        HttpResponseMessage response = await host.Client.PostAsync("/api/v1/server/restart", null).ConfigureAwait(false);
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AssertFalse(body.Contains("shutting_down", StringComparison.Ordinal), "Restart must not report the shutdown status");
                    }
                }
            });
        }

        /// <summary>
        /// A loopback webserver with only <see cref="StatusRoutes"/> registered and an <see cref="HttpClient"/>
        /// bound to it.
        /// </summary>
        private sealed class StatusRouteHost : IDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            /// <summary>Client bound to this host.</summary>
            public HttpClient Client { get; }

            private StatusRouteHost(Webserver server, CancellationTokenSource cancellation, HttpClient client)
            {
                _Server = server;
                _Cancellation = cancellation;
                Client = client;
            }

            /// <summary>Start a host serving the status routes.</summary>
            /// <param name="database">Test database.</param>
            /// <param name="settings">Application settings.</param>
            /// <param name="stopCallback">Graceful stop callback the routes invoke.</param>
            /// <returns>The running host.</returns>
            public static StatusRouteHost Start(TestDatabase database, ArmadaSettings settings, Action stopCallback)
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AuthenticationService authentication = new AuthenticationService(database.Driver, new SessionTokenService(), settings, logging);
                RecordingAdmiral admiral = RecordingAdmiral.Create();
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

                int port = ReservePort();
                WebserverSettings webserverSettings = new WebserverSettings();
                webserverSettings.Hostname = "127.0.0.1";
                webserverSettings.Port = port;
                Webserver server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.Send().ConfigureAwait(false);
                });

                Func<HttpContextBase, Task<AuthContext>> authenticate = ctx => authentication.AuthenticateAsync(
                    ctx.Request.Headers.Get("Authorization"),
                    ctx.Request.Headers.Get("X-Token"),
                    ctx.Request.Headers.Get("X-Api-Key"));
                StatusRoutes routes = new StatusRoutes(database.Driver, settings, admiral.Service, stopCallback, DateTime.UtcNow, jsonOptions, logging);
                routes.Register(server, authenticate, new AuthorizationService());

                CancellationTokenSource cancellation = new CancellationTokenSource();
                server.Start(cancellation.Token);
                HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(10) };
                return new StatusRouteHost(server, cancellation, client);
            }

            /// <summary>Dispose the host.</summary>
            public void Dispose()
            {
                Client.Dispose();
                _Cancellation.Cancel();
                try { _Server.Stop(); }
                catch (Exception exception) { Console.WriteLine("Status route test server stop failed: " + exception.Message); }
                _Server.Dispose();
                _Cancellation.Dispose();
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
    }
}
