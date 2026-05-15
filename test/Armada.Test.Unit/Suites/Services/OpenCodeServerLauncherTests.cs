namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests daemon launch behavior for OpenCode server inference mode.
    /// </summary>
    public class OpenCodeServerLauncherTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "OpenCode Server Launcher";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("StartAsync_HealthyDaemonAlreadyRunning_DoesNotSpawn", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(0, runner.StartCallCount, "Launcher should attach to existing daemon without spawning.");
                AssertEqual(1, handler.RequestUris.Count, "Launcher should probe health once.");
            });

            await RunTest("StartAsync_AutoLaunchFalse_NoOp", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", false), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(0, runner.StartCallCount, "AutoLaunch false should bypass process spawning.");
                AssertEqual(0, handler.RequestUris.Count, "AutoLaunch false should skip health probes.");
            });

            await RunTest("StartAsync_InferenceClientHttp_NoOp", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("Http", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(0, runner.StartCallCount, "Http inference mode should bypass launcher.");
                AssertEqual(0, handler.RequestUris.Count, "Http inference mode should skip health probes.");
            });

            await RunTest("StartAsync_InitiallyUnhealthy_SpawnsAndBecomesHealthy", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(1, runner.StartCallCount, "Launcher should spawn daemon when health probe is not ready.");
                AssertTrue(runner.LastStartInfo != null, "Start info should be captured.");
                AssertContains("serve --port 4096 --hostname 127.0.0.1", runner.LastStartInfo!.Arguments, "Serve args must include configured host and port.");
            });
        }

        private static ArmadaSettings BuildSettings(string inferenceClient, bool autoLaunch)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.CodeIndex.InferenceClient = inferenceClient;
            settings.CodeIndex.OpenCodeServer.AutoLaunch = autoLaunch;
            settings.CodeIndex.OpenCodeServer.BaseUrl = "http://127.0.0.1:4096";
            settings.CodeIndex.OpenCodeServer.Port = 4096;
            settings.CodeIndex.OpenCodeServer.Hostname = "127.0.0.1";
            settings.CodeIndex.OpenCodeServer.RequestTimeoutSeconds = 5;
            settings.CodeIndex.OpenCodeServer.StartupTimeoutSeconds = 5;
            return settings;
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Task NoDelayAsync(TimeSpan delay, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<ResponseScript> _Scripts = new Queue<ResponseScript>();

            public List<string> RequestUris { get; } = new List<string>();

            public void Enqueue(HttpStatusCode statusCode, string body)
            {
                ResponseScript script = new ResponseScript();
                script.StatusCode = statusCode;
                script.Body = body;
                _Scripts.Enqueue(script);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
                if (_Scripts.Count < 1) throw new InvalidOperationException("No scripted response available.");

                ResponseScript script = _Scripts.Dequeue();
                HttpResponseMessage response = new HttpResponseMessage(script.StatusCode)
                {
                    Content = new StringContent(script.Body ?? string.Empty, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }

        private sealed class ResponseScript
        {
            public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
            public string? Body { get; set; }
        }

        private sealed class RecordingProcessRunner : OpenCodeServerLauncher.IProcessRunner
        {
            public int StartCallCount { get; private set; }
            public ProcessStartInfo? LastStartInfo { get; private set; }

            public OpenCodeServerLauncher.ILaunchedProcess? Start(ProcessStartInfo startInfo)
            {
                StartCallCount++;
                LastStartInfo = startInfo;
                return new RecordingLaunchedProcess();
            }
        }

        private sealed class RecordingLaunchedProcess : OpenCodeServerLauncher.ILaunchedProcess
        {
            public int ProcessId => 12345;

            public bool HasExited { get; private set; }

            public Task<string> DrainStandardOutputAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public Task<string> DrainStandardErrorAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public void Kill(bool entireProcessTree)
            {
                HasExited = true;
            }

            public bool WaitForExit(int milliseconds)
            {
                HasExited = true;
                return true;
            }

            public void Dispose()
            {
            }
        }
    }
}
