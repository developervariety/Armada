namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Health cutover probe against a real local HTTP listener.
    /// </summary>
    public sealed class SelfDeployHttpHealthProbeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Health Probe";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            DateTime launchedUtc = DateTime.UtcNow;
            SelfDeployProcessIdentity launched = new SelfDeployProcessIdentity { ProcessId = 1, StartedUtc = launchedUtc };

            await RunTest("CheckAsync_HealthyServerStartedAfterLaunch_IsHealthy", async () =>
            {
                using (HealthServer server = HealthServer.Json(200, "healthy", launchedUtc.AddSeconds(2)))
                {
                    SelfDeployHealthResult result = await Probe(TimeSpan.FromSeconds(5)).CheckAsync(server.Url, launched);
                    AssertTrue(result.Healthy, "healthy server accepted: " + result.FailureReason);
                }
            });

            await RunTest("CheckAsync_ServerStartedBeforeLaunch_IsRejected", async () =>
            {
                using (HealthServer server = HealthServer.Json(200, "healthy", launchedUtc.AddMinutes(-5)))
                {
                    SelfDeployHealthResult result = await Probe(TimeSpan.FromSeconds(5)).CheckAsync(server.Url, launched);
                    AssertFalse(result.Healthy, "server that predates the launch is not the launched process");
                    AssertEqual("health_start_predates_launch", result.FailureReason, "reason");
                }
            });

            await RunTest("CheckAsync_UnhealthyStatusOrHttpError_IsRejected", async () =>
            {
                using (HealthServer degraded = HealthServer.Json(200, "degraded", launchedUtc.AddSeconds(1)))
                using (HealthServer failing = HealthServer.Json(500, "healthy", launchedUtc.AddSeconds(1)))
                {
                    AssertEqual("health_status_not_healthy", (await Probe(TimeSpan.FromSeconds(5)).CheckAsync(degraded.Url, launched)).FailureReason, "status reason");
                    AssertEqual("health_http_500", (await Probe(TimeSpan.FromSeconds(5)).CheckAsync(failing.Url, launched)).FailureReason, "http reason");
                }
            });

            await RunTest("CheckAsync_UnreachableEndpoint_IsRejected", async () =>
            {
                string url = "http://127.0.0.1:" + FreePort() + "/api/v1/status/health";
                SelfDeployHealthResult result = await Probe(TimeSpan.FromSeconds(5)).CheckAsync(url, launched);
                AssertEqual("health_unreachable", result.FailureReason, "reason");
            });

            await RunTest("CheckAsync_SlowServer_BoundedByAttemptTimeout", async () =>
            {
                using (HealthServer server = HealthServer.Delayed(TimeSpan.FromSeconds(3)))
                {
                    DateTime started = DateTime.UtcNow;
                    SelfDeployHealthResult result = await Probe(TimeSpan.FromMilliseconds(300)).CheckAsync(server.Url, launched);
                    AssertEqual("health_attempt_timeout", result.FailureReason, "reason");
                    AssertTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "attempt returned within its bound");
                }
            });

            await RunTest("CheckAsync_OversizedResponse_IsRejected", async () =>
            {
                string body = "{\"Status\":\"healthy\",\"Pad\":\"" + new string('x', SelfDeployHttpHealthProbe.MaximumResponseBytes + 10) + "\"}";
                using (HealthServer server = new HealthServer(200, body, TimeSpan.Zero))
                {
                    SelfDeployHealthResult result = await Probe(TimeSpan.FromSeconds(5)).CheckAsync(server.Url, launched);
                    AssertEqual("health_response_too_large", result.FailureReason, "reason");
                }
            });
        }

        private static SelfDeployHttpHealthProbe Probe(TimeSpan attemptTimeout)
        {
            return new SelfDeployHttpHealthProbe(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, attemptTimeout);
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class HealthServer : IDisposable
        {
            private readonly HttpListener _Listener = new HttpListener();
            private readonly CancellationTokenSource _Stop = new CancellationTokenSource();

            public HealthServer(int statusCode, string body, TimeSpan delay)
            {
                string prefix = "http://127.0.0.1:" + FreePort() + "/";
                _Listener.Prefixes.Add(prefix);
                _Listener.Start();
                Url = prefix + "api/v1/status/health";
                _ = Task.Run(() => ServeAsync(statusCode, body, delay));
            }

            public string Url { get; }

            public static HealthServer Json(int statusCode, string status, DateTime startUtc)
            {
                return new HealthServer(statusCode, "{\"Status\":\"" + status + "\",\"StartUtc\":\"" + startUtc.ToString("o") + "\"}", TimeSpan.Zero);
            }

            public static HealthServer Delayed(TimeSpan delay)
            {
                return new HealthServer(200, "{\"Status\":\"healthy\",\"StartUtc\":\"" + DateTime.UtcNow.AddHours(1).ToString("o") + "\"}", delay);
            }

            private async Task ServeAsync(int statusCode, string body, TimeSpan delay)
            {
                while (_Listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _Listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException)
                    {
                        return;
                    }

                    try
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, _Stop.Token);
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.StatusCode = statusCode;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is System.IO.IOException)
                    {
                        // The probe under test abandons slow responses by design; the listener only reports that.
                        Console.Error.WriteLine("[SelfDeployHttpHealthProbeTests] response abandoned: " + ex.GetType().Name);
                    }
                }
            }

            public void Dispose()
            {
                _Stop.Cancel();
                _Listener.Close();
                _Stop.Dispose();
            }
        }
    }
}
